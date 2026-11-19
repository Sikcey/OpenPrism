//go:build windows && with_gvisor

package main

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/sagernet/gvisor/pkg/buffer"
	"github.com/sagernet/gvisor/pkg/tcpip/header"
	"github.com/sagernet/gvisor/pkg/tcpip/link/channel"
	"github.com/sagernet/gvisor/pkg/tcpip/stack"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/common/process"
	"github.com/sagernet/sing-box/log"
	tun "github.com/sagernet/sing-tun"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
	"github.com/sagernet/sing/common/winiphlpapi"
)

type capturedFlow struct {
	owner   *adapter.ConnectionOwner
	address packetAddress
	expires time.Time
	close   func() error
}
type packetEngine struct {
	ctx                      context.Context
	cancel                   context.CancelFunc
	router                   adapter.Router
	policy                   *Policy
	driver                   packetDriver
	searcher                 process.Searcher
	ep                       *channel.Endpoint
	stack                    tun.Stack
	mu                       sync.Mutex
	flows                    map[flowKey]*capturedFlow
	selectedByPath           map[string]uint64 // nil in production; attribution assertions in the isolated self-test
	selectedTCP, selectedUDP uint64
	replies                  uint64
	sniffTCP                 bool
	errors                   chan error
	wg                       sync.WaitGroup
	once                     sync.Once
}

func newPacketEngine(parent context.Context, router adapter.Router, policy *Policy, dll string) (*packetEngine, error) {
	return createPacketEngine(parent, router, policy, func() (packetDriver, error) { return openDriver(dll) })
}
func createPacketEngine(parent context.Context, router adapter.Router, policy *Policy, driverFactory func() (packetDriver, error)) (*packetEngine, error) {
	ctx, cancel := context.WithCancel(parent)
	e := &packetEngine{ctx: ctx, cancel: cancel, router: router, policy: policy, ep: channel.New(256, 65535, ""), flows: make(map[flowKey]*capturedFlow), errors: make(chan error, 1)}
	logger := log.NewNOPFactory().NewLogger("selected-packets")
	if err := winiphlpapi.LoadExtendedTable(); err != nil {
		cancel()
		return nil, err
	}
	searcher := &nativeSearcher{}
	e.searcher = searcher
	s, err := tun.NewStack("gvisor", tun.StackOptions{Context: ctx, Tun: e, TunOptions: tun.Options{Name: "WGTun-selected", MTU: 65535, Inet4Address: []netip.Prefix{netip.MustParsePrefix("172.28.199.1/30")}, Inet6Address: []netip.Prefix{netip.MustParsePrefix("fdfe:7767:7475::1/126")}}, Handler: e, Logger: logger, UDPTimeout: time.Minute, ICMPTimeout: time.Second})
	if err != nil {
		searcher.Close()
		cancel()
		return nil, err
	}
	e.stack = s
	// Start the stack before opening any interception filter.
	if err = s.Start(); err != nil {
		e.Close()
		return nil, err
	}
	d, err := driverFactory()
	if err != nil {
		e.Close()
		return nil, err
	}
	e.driver = d
	return e, nil
}
func (e *packetEngine) fail(err error) {
	select {
	case e.errors <- err:
	default:
	}
}
func (e *packetEngine) Start() error {
	e.wg.Add(3)
	go func() {
		defer e.wg.Done()
		ticker := time.NewTicker(time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-e.ctx.Done():
				return
			case now := <-ticker.C:
				var closers []func() error
				e.mu.Lock()
				for key, flow := range e.flows {
					if !flow.expires.After(now) {
						delete(e.flows, key)
						if flow.close != nil {
							closers = append(closers, flow.close)
						}
					}
				}
				e.mu.Unlock()
				for _, close := range closers {
					close()
				}
			}
		}
	}()
	go func() { defer e.wg.Done(); e.capture() }()
	go func() {
		defer e.wg.Done()
		for {
			p := e.ep.ReadContext(e.ctx)
			if p == nil {
				return
			}
			var b []byte
			for _, s := range p.AsSlices() {
				b = append(b, s...)
			}
			p.DecRef()
			if _, err := e.Write(b); err != nil {
				e.fail(err)
				return
			}
		}
	}()
	return nil
}
func (e *packetEngine) capture() {
	b := make([]byte, 65535)
	for {
		n, address, err := e.driver.Read(b)
		if err != nil {
			if e.ctx.Err() == nil {
				e.fail(err)
			}
			return
		}
		packet := b[:n]
		parsed, err := parsePacket(packet)
		if err != nil {
			if err = e.driver.Write(packet, address); err != nil {
				e.fail(err)
				return
			}
			continue
		}
		key := parsed.key
		if key.source.Addr().IsLinkLocalUnicast() || key.destination.Addr().IsLinkLocalUnicast() || key.destination.Addr().IsMulticast() {
			if err = e.driver.Write(packet, address); err != nil {
				e.fail(err)
				return
			}
			continue
		}
		network := "udp"
		if key.protocol == 6 {
			network = "tcp"
		}
		owner, err := e.searcher.FindProcessInfo(e.ctx, network, key.source, key.destination)
		// Unknown ownership is direct; never silently apply another process's rule.
		selected := false
		if err == nil && owner != nil && owner.ProcessID != uint32(os.Getpid()) && owner.ProcessPath != "" {
			metadata := adapter.InboundContext{Network: network, Source: M.SocksaddrFromNetIP(key.source), Destination: M.SocksaddrFromNetIP(key.destination), ProcessInfo: owner}
			selected = e.policy.Decide(metadata).Intercept
		}
		var closeOld func() error
		e.mu.Lock()
		previous := e.flows[key]
		if previous != nil && owner != nil && previous.owner.ProcessID != owner.ProcessID {
			if previous.close != nil {
				closeOld = previous.close
			}
			delete(e.flows, key)
			previous = nil
		}
		// Never move an already established TCP socket into a newly created stack.
		if key.protocol == 6 && previous == nil && !parsed.syn {
			selected = false
		}
		if selected {
			if key.protocol == 6 {
				e.selectedTCP++
			} else {
				e.selectedUDP++
			}
			if e.selectedByPath != nil {
				e.selectedByPath[owner.ProcessPath]++
			}
			if previous == nil {
				previous = &capturedFlow{owner: owner}
				e.flows[key] = previous
			}
			previous.owner = owner
			previous.address = address
			previous.expires = time.Now().Add(24 * time.Hour)
			if key.protocol == 17 {
				previous.expires = time.Now().Add(2 * time.Minute)
			}
		} else if previous != nil {
			if previous.close != nil {
				closeOld = previous.close
			}
			delete(e.flows, key)
		}
		if len(e.flows) > 65536 {
			e.mu.Unlock()
			e.fail(fmt.Errorf("flow limit reached; releasing capture"))
			return
		}
		e.mu.Unlock()
		if closeOld != nil {
			closeOld()
		}
		if !selected {
			if err = e.driver.Write(packet, address); err != nil {
				e.fail(err)
				return
			}
			continue
		}
		// Windows checksum offload can leave invalid TCP/UDP checksums in an
		// outbound capture. Finalize them before handing it to gVisor.
		if err = e.driver.Normalize(packet, &address); err != nil {
			e.fail(err)
			return
		}
		p := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(append([]byte(nil), packet...))})
		protocol := header.IPv4ProtocolNumber
		if packet[0]>>4 == 6 {
			protocol = header.IPv6ProtocolNumber
		}
		e.ep.InjectInbound(protocol, p)
		p.DecRef()
	}
}
func (e *packetEngine) Name() (string, error)                { return "WGTun-selected-memory-tun", nil }
func (e *packetEngine) UpdateRouteOptions(tun.Options) error { return nil }
func (e *packetEngine) Read([]byte) (int, error)             { return 0, io.EOF }
func (e *packetEngine) Write(b []byte) (int, error) {
	parsed, err := parsePacket(b)
	if err != nil {
		return 0, err
	}
	e.mu.Lock()
	e.replies++
	flow := e.flows[parsed.key.reverse()]
	if flow == nil {
		e.mu.Unlock()
		return len(b), nil
	}
	address := flow.address
	owner := *flow.owner
	e.mu.Unlock()
	key := parsed.key.reverse()
	network := "udp"
	if key.protocol == 6 {
		network = "tcp"
	}
	current, lookupErr := e.searcher.FindProcessInfo(e.ctx, network, key.source, key.destination)
	if lookupErr != nil || current == nil || current.ProcessID != owner.ProcessID || !strings.EqualFold(current.ProcessPath, owner.ProcessPath) {
		return len(b), nil
	}
	if address[1]&(uint64(1)<<18) == 0 {
		address[1] &= ^(uint64(1) << 17)
	} // Loopback injection is outbound; ordinary replies are inbound.
	if err = e.driver.Write(b, address); err != nil {
		return 0, err
	}
	return len(b), nil
}
func (e *packetEngine) WritePacket(p *stack.PacketBuffer) (int, error) {
	var b []byte
	for _, part := range p.AsSlices() {
		b = append(b, part...)
	}
	return e.Write(b)
}
func (e *packetEngine) NewEndpoint() (stack.LinkEndpoint, stack.NICOptions, error) {
	return e.ep, stack.NICOptions{}, nil
}
func (e *packetEngine) Close() error {
	e.once.Do(func() {
		e.cancel()
		if e.driver != nil {
			e.driver.Close()
		}
		if e.stack != nil {
			e.stack.Close()
		}
		e.ep.Close()
		if e.searcher != nil {
			e.searcher.Close()
		}
		e.wg.Wait()
	})
	return nil
}
func (e *packetEngine) JudgeFlow(uint8, netip.AddrPort, netip.AddrPort, []byte) tun.FlowVerdict {
	return tun.FlowVerdict{Action: tun.ActionAccept}
}
func (e *packetEngine) NewDNSPacket([]byte, M.Socksaddr, M.Socksaddr, N.PacketWriter) {}
func (e *packetEngine) metadata(network string, source, destination M.Socksaddr, close func() error) (adapter.InboundContext, bool) {
	protocol := uint8(17)
	if network == "tcp" {
		protocol = 6
	}
	key := flowKey{source.AddrPort(), destination.AddrPort(), protocol}
	e.mu.Lock()
	defer e.mu.Unlock()
	flow := e.flows[key]
	if flow == nil {
		return adapter.InboundContext{}, false
	}
	flow.close = close
	if destination.Addr.IsLinkLocalUnicast() && destination.Addr.Is6() {
		index := int(uint32(flow.address[2]))
		if index > 0 {
			destination.Addr = destination.Addr.WithZone(strconv.Itoa(index))
		}
	}
	return adapter.InboundContext{Inbound: "selected-packets", InboundType: "tun", Network: network, Source: source, Destination: destination, ProcessInfo: flow.owner}, true
}
func (e *packetEngine) NewConnectionEx(ctx context.Context, c net.Conn, source, destination M.Socksaddr, onClose N.CloseHandlerFunc) {
	metadata, ok := e.metadata("tcp", source, destination, c.Close)
	if !ok {
		c.Close()
		return
	}
	if e.sniffTCP {
		c, metadata = sniffDestination(ctx, c, metadata)
	}
	e.router.RouteConnectionEx(ctx, c, metadata, onClose)
}
func (e *packetEngine) NewPacketConnectionEx(ctx context.Context, c N.PacketConn, source, destination M.Socksaddr, onClose N.CloseHandlerFunc) {
	metadata, ok := e.metadata("udp", source, destination, c.Close)
	if !ok {
		c.Close()
		return
	}
	e.router.RoutePacketConnectionEx(ctx, c, metadata, onClose)
}
