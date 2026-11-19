//go:build windows && with_gvisor

// WGTun.Tun embeds sing-box. Only policy-selected packets enter its sing-tun
// stack. There is no default-route TUN adapter and no system DNS modification.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	sbjson "github.com/sagernet/sing/common/json"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run(args []string) error {
	if len(args) == 0 {
		return fmt.Errorf("expected check or run")
	}
	if args[0] == "self-test" {
		return selfTest(args[1:])
	}
	if args[0] == "probe" {
		return probe(args[1:])
	}
	if args[0] == "capture-worker" {
		return captureWorker(args[1:])
	}
	command := args[0]
	flags := flag.NewFlagSet(command, flag.ContinueOnError)
	config := flags.String("c", "", "configuration")
	driver := flags.String("driver", "", "absolute WinDivert DLL path")
	lease := flags.String("lease", "", "broker heartbeat path")
	ready := flags.String("ready", "", "readiness path")
	sniffTCP := flags.Bool("sniff", false, "selected TCP TLS/HTTP destination re-resolution")
	if err := flags.Parse(args[1:]); err != nil {
		return err
	}
	if command != "check" && command != "run" {
		return fmt.Errorf("unsupported command")
	}
	raw, err := os.ReadFile(*config)
	if err != nil {
		return err
	}
	var envelope struct {
		Route struct {
			Rules []json.RawMessage `json:"rules"`
			Final string            `json:"final"`
		} `json:"route"`
	}
	if err = json.Unmarshal(raw, &envelope); err != nil {
		return err
	}
	policy, err := CompileWindows(Export{Rules: envelope.Route.Rules, Final: envelope.Route.Final})
	if err != nil {
		return fmt.Errorf("packet policy: %w", err)
	}
	ctx, cancel := context.WithCancel(include.Context(context.Background()))
	defer cancel()
	options, err := sbjson.UnmarshalExtendedContext[option.Options](ctx, raw)
	if err != nil {
		return err
	}
	// Packet capture is the sole ingress. Never accept an accidental auto-route
	// TUN or exposed proxy listener from a stale/foreign configuration.
	if len(options.Inbounds) != 0 {
		return fmt.Errorf("selective packet engine requires zero OS/proxy inbounds")
	}
	// Selection/direct precedence is already enforced before packet ingress.
	// Every flow reaching this router is positive-selected and uses active.
	options.Route.Rules = nil
	options.Route.Final = "active"
	if *sniffTCP {
		options.Route.Rules, err = sbjson.UnmarshalExtendedContext[[]option.Rule](ctx, []byte(`[{"action":"resolve","server":"dns-upstream"}]`))
		if err != nil {
			return err
		}
	}
	instance, err := box.New(box.Options{Options: options, Context: ctx})
	if err != nil {
		return err
	}
	defer instance.Close()
	if command == "check" {
		return nil
	}
	if !filepath.IsAbs(*driver) || *lease == "" || *ready == "" {
		return fmt.Errorf("driver, lease and ready paths required")
	}
	if err = instance.Start(); err != nil {
		return err
	}
	engine, err := newPacketEngine(ctx, instance.Router(), policy, *driver)
	if err != nil {
		return err
	}
	defer engine.Close()
	engine.sniffTCP = *sniffTCP
	if err = engine.Start(); err != nil {
		return err
	}
	if err = os.WriteFile(*ready, []byte(`{"ready":true,"backend":"sing-box-selective-tun"}`), 0600); err != nil {
		return err
	}
	defer os.Remove(*ready)
	timer := time.NewTicker(500 * time.Millisecond)
	defer timer.Stop()
	var heartbeat leaseTracker
	for {
		select {
		case err = <-engine.errors:
			return err
		case <-timer.C:
			info, e := os.Stat(*lease)
			if e != nil || !heartbeat.observe(time.Now(), info.ModTime()) {
				return fmt.Errorf("broker heartbeat ended; capture released")
			}
			if _, e = os.Stat(*lease + ".stop"); e == nil {
				return nil
			}
		}
	}
}
