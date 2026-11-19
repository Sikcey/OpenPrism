//go:build windows && with_gvisor

package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	sbjson "github.com/sagernet/sing/common/json"
	"os"
	"os/exec"
	"strings"
	"syscall"
	"time"
)

// Isolated test-only worker. Its filter is supplied by self-test and names only
// the self-test's local server ports. The parent intentionally hard-kills it.
func captureWorker(args []string) error {
	if len(args) != 3 {
		return fmt.Errorf("capture-worker requires driver, local fixture filter and selected fixture exe")
	}
	if !strings.HasPrefix(args[1], "outbound and loopback and ") {
		return fmt.Errorf("worker only accepts loopback fixture filters")
	}
	ctx, cancel := context.WithCancel(include.Context(context.Background()))
	defer cancel()
	rawRule, _ := json.Marshal(map[string]any{"process_path": []string{args[2]}, "action": "route", "outbound": "active"})
	policy, err := CompileWindows(Export{Final: "direct", Rules: []json.RawMessage{rawRule}})
	if err != nil {
		return err
	}
	raw := []byte(`{"log":{"disabled":true},"dns":{"servers":[{"type":"local","tag":"local"}]},"outbounds":[{"type":"direct","tag":"active"}],"route":{"final":"active","default_domain_resolver":"local"}}`)
	options, err := sbjson.UnmarshalExtendedContext[option.Options](ctx, raw)
	if err != nil {
		return err
	}
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		return err
	}
	defer instance.Close()
	if err = instance.Start(); err != nil {
		return err
	}
	engine, err := createPacketEngine(ctx, instance.Router(), policy, func() (packetDriver, error) { return openDriverFilter(args[0], args[1]) })
	if err != nil {
		return err
	}
	defer engine.Close()
	engine.Start()
	fmt.Println("READY")
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		if scanner.Text() != "stats" {
			return fmt.Errorf("unknown worker operation")
		}
		engine.mu.Lock()
		fmt.Printf("COUNTS %d %d\n", engine.selectedTCP, engine.selectedUDP)
		engine.mu.Unlock()
	}
	return scanner.Err()
}

func testHardKill(ctx context.Context, executable, driver, filter, selected, udpTarget string) error {
	deadline, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	worker := exec.CommandContext(deadline, executable, "capture-worker", driver, filter, selected)
	worker.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
	worker.Stderr = os.Stderr
	input, err := worker.StdinPipe()
	if err != nil {
		return err
	}
	output, err := worker.StdoutPipe()
	if err != nil {
		return err
	}
	if err = worker.Start(); err != nil {
		return err
	}
	waited := false
	defer func() {
		input.Close()
		if !waited {
			worker.Process.Kill()
			worker.Wait()
		}
	}()
	reader := bufio.NewReader(output)
	line, err := reader.ReadString('\n')
	if err != nil || strings.TrimSpace(line) != "READY" {
		return fmt.Errorf("worker not ready: %s %v", line, err)
	}
	runProbe := func(payload string) error {
		command := exec.CommandContext(deadline, selected, "probe", "udp4", udpTarget, payload)
		command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
		data, e := command.CombinedOutput()
		if e != nil {
			return fmt.Errorf("worker probe: %w %s", e, data)
		}
		return nil
	}
	if err = runProbe("before-hard-kill"); err != nil {
		return err
	}
	fmt.Fprintln(input, "stats")
	line, err = reader.ReadString('\n')
	if err != nil {
		return err
	}
	var tcp, udp uint64
	if _, err = fmt.Sscanf(line, "COUNTS %d %d", &tcp, &udp); err != nil || udp == 0 {
		return fmt.Errorf("worker never captured selected UDP: %s", line)
	}
	if err = worker.Process.Kill(); err != nil {
		return err
	}
	worker.Wait()
	waited = true
	if err = runProbe("after-hard-kill"); err != nil {
		return err
	}
	fmt.Println("PASS hard-kill: original native path restored when capture worker was terminated without cleanup")
	return nil
}
