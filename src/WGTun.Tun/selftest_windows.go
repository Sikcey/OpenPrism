//go:build windows && with_gvisor

package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	sbjson "github.com/sagernet/sing/common/json"
)

func probe(args []string) error {
	if len(args) != 3 {
		return fmt.Errorf("probe needs protocol, numeric endpoint, payload")
	}
	c, err := net.DialTimeout(args[0], args[1], 3*time.Second)
	if err != nil {
		return err
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(4 * time.Second))
	if _, err = c.Write([]byte(args[2])); err != nil {
		return err
	}
	b := make([]byte, 2048)
	n, err := c.Read(b)
	if err != nil {
		return err
	}
	if string(b[:n]) != "echo:"+args[2] {
		return fmt.Errorf("unexpected echo %q", b[:n])
	}
	return nil
}

func selfTest(args []string) error {
	flags := flag.NewFlagSet("self-test", flag.ContinueOnError)
	dll := flags.String("driver", "", "driver DLL")
	ipv6 := flags.Bool("ipv6", false, "test IPv6 on a local assigned address")
	if err := flags.Parse(args); err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	route, err := net.DialUDP("udp4", nil, &net.UDPAddr{IP: net.IPv4(192, 0, 2, 1), Port: 9})
	if err != nil {
		return err
	}
	ip := route.LocalAddr().(*net.UDPAddr).IP
	route.Close()
	zone := ""
	tcpNetwork, udpNetwork := "tcp4", "udp4"
	family := "ip"
	addressField := "ip.DstAddr"
	if *ipv6 {
		found := false
		interfaces, e := net.Interfaces()
		if e != nil {
			return e
		}
		for _, iface := range interfaces {
			if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
				continue
			}
			addresses, _ := iface.Addrs()
			for _, address := range addresses {
				candidate, _, e := net.ParseCIDR(address.String())
				if e == nil && candidate.To4() == nil && !candidate.IsLoopback() && !candidate.IsLinkLocalUnicast() {
					ip = candidate
					zone = iface.Name
					found = true
					break
				}
			}
			if found {
				break
			}
		}
		if !found {
			fmt.Println("SKIP native IPv6: this host has no assigned non-link-local IPv6 address")
			return nil
		}
		tcpNetwork, udpNetwork = "tcp6", "udp6"
		family = "ipv6"
		addressField = "ipv6.DstAddr"
	}
	if ip.IsLoopback() || ip.IsUnspecified() {
		return fmt.Errorf("non-loopback local IPv4 address required")
	}
	tcp, err := net.ListenTCP(tcpNetwork, &net.TCPAddr{IP: ip, Zone: zone})
	if err != nil {
		return err
	}
	defer tcp.Close()
	udp, err := net.ListenUDP(udpNetwork, &net.UDPAddr{IP: ip, Zone: zone})
	if err != nil {
		return err
	}
	defer udp.Close()
	go func() {
		for {
			c, e := tcp.AcceptTCP()
			if e != nil {
				return
			}
			go func() {
				defer c.Close()
				c.SetDeadline(time.Now().Add(4 * time.Second))
				b := make([]byte, 2048)
				n, e := c.Read(b)
				if e == nil {
					c.Write(append([]byte("echo:"), b[:n]...))
				}
			}()
		}
	}()
	go func() {
		b := make([]byte, 2048)
		for {
			n, a, e := udp.ReadFromUDP(b)
			if e != nil {
				return
			}
			udp.WriteToUDP(append([]byte("echo:"), b[:n]...), a)
		}
	}()
	directory, err := os.MkdirTemp("", "WGTun-native-check-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(directory)
	executable, err := os.Executable()
	if err != nil {
		return err
	}
	children := []string{filepath.Join(directory, "selected", "app.exe"), filepath.Join(directory, "ignored", "app.exe"), filepath.Join(directory, "selected", "direct.exe")}
	for _, path := range children {
		os.MkdirAll(filepath.Dir(path), 0700)
		input, e := os.Open(executable)
		if e != nil {
			return e
		}
		output, e := os.Create(path)
		if e != nil {
			input.Close()
			return e
		}
		_, e = io.Copy(output, input)
		input.Close()
		output.Close()
		if e != nil {
			return e
		}
	}
	pathRule, _ := json.Marshal(map[string]any{"process_path_regex": []string{"(?i)^" + regexpQuote(filepath.Join(directory, "selected")+string(os.PathSeparator))}, "action": "route", "outbound": "active"})
	directRule, _ := json.Marshal(map[string]any{"process_path": []string{children[2]}, "action": "route", "outbound": "direct"})
	policy, err := CompileWindows(Export{Final: "direct", Rules: []json.RawMessage{directRule, pathRule}})
	if err != nil {
		return err
	}
	raw := []byte(`{"log":{"disabled":true},"dns":{"servers":[{"type":"local","tag":"local"}]},"outbounds":[{"type":"direct","tag":"active"}],"route":{"final":"active","default_domain_resolver":"local"}}`)
	boxContext := include.Context(ctx)
	options, err := sbjson.UnmarshalExtendedContext[option.Options](boxContext, raw)
	if err != nil {
		return err
	}
	instance, err := box.New(box.Options{Context: boxContext, Options: options})
	if err != nil {
		return err
	}
	defer instance.Close()
	if err = instance.Start(); err != nil {
		return err
	}
	filter := fmt.Sprintf("outbound and loopback and %s and %s == %s and ((tcp and tcp.DstPort == %d) or (udp and udp.DstPort == %d))", family, addressField, ip, tcp.Addr().(*net.TCPAddr).Port, udp.LocalAddr().(*net.UDPAddr).Port)
	engine, err := createPacketEngine(ctx, instance.Router(), policy, func() (packetDriver, error) { return openDriverFilter(*dll, filter) })
	if err != nil {
		return err
	}
	defer engine.Close()
	engine.selectedByPath = make(map[string]uint64)
	if err = engine.Start(); err != nil {
		return err
	}
	for i, child := range children {
		for _, network := range []string{tcpNetwork, udpNetwork} {
			target := tcp.Addr().String()
			if network == udpNetwork {
				target = udp.LocalAddr().String()
			}
			command := exec.CommandContext(ctx, child, "probe", network, target, fmt.Sprintf("application-%d-%s", i, network))
			command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
			if output, e := command.CombinedOutput(); e != nil {
				engine.mu.Lock()
				counts := fmt.Sprintf("tcp=%d udp=%d replies=%d flows=%d", engine.selectedTCP, engine.selectedUDP, engine.replies, len(engine.flows))
				engine.mu.Unlock()
				select {
				case cause := <-engine.errors:
					return fmt.Errorf("%s: engine error %v; %w %s", counts, cause, e, output)
				default:
				}
				return fmt.Errorf("%s %s %s: %w %s", child, network, counts, e, output)
			}
			fmt.Printf("PASS %s application %d real TCP/UDP round trip\n", network, i)
		}
	}
	engine.mu.Lock()
	selected := engine.selectedByPath[children[0]]
	wrong := engine.selectedByPath[children[1]] + engine.selectedByPath[children[2]]
	tcpSelected, udpSelected := engine.selectedTCP, engine.selectedUDP
	engine.mu.Unlock()
	if selected == 0 || wrong != 0 || tcpSelected == 0 || udpSelected == 0 {
		return fmt.Errorf("selection attribution failed: selected=%d wrong=%d tcp=%d udp=%d", selected, wrong, tcpSelected, udpSelected)
	}
	if err = engine.Close(); err != nil {
		return err
	}
	command := exec.CommandContext(ctx, children[0], "probe", udpNetwork, udp.LocalAddr().String(), "after-disconnect")
	command.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000}
	if output, e := command.CombinedOutput(); e != nil {
		return fmt.Errorf("disconnect direct recovery: %w %s", e, output)
	}
	fmt.Println("PASS selective production engine: real process rules, TCP, UDP, direct exclusions, filter close; no route/DNS changes")
	if !*ipv6 {
		if err = testHardKill(ctx, executable, *dll, filter, children[0], udp.LocalAddr().String()); err != nil {
			return err
		}
	}
	if !*ipv6 {
		return selfTest(append(args, "-ipv6"))
	}
	return nil
}
func regexpQuote(value string) string {
	replacer := strings.NewReplacer(`\`, `\\`, `.`, `\.`, `+`, `\+`, `(`, `\(`, `)`, `\)`, `[`, `\[`, `]`, `\]`, `{`, `\{`, `}`, `\}`, `^`, `\^`, `$`, `\$`, `|`, `\|`, `?`, `\?`, `*`, `\*`)
	return replacer.Replace(value)
}
