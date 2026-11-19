//go:build windows && with_gvisor

package main

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net/netip"
	"sync"
	"testing"
	"time"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing/common/buf"
	N "github.com/sagernet/sing/common/network"
)

type testPacket struct {
	data    []byte
	address packetAddress
}
type testDriver struct {
	in, out chan testPacket
	stop    chan struct{}
	once    sync.Once
}

func (d *testDriver) Read(b []byte) (int, packetAddress, error) {
	select {
	case p := <-d.in:
		return copy(b, p.data), p.address, nil
	case <-d.stop:
		return 0, packetAddress{}, io.EOF
	}
}
func (d *testDriver) Write(b []byte, a packetAddress) error {
	select {
	case d.out <- testPacket{append([]byte(nil), b...), a}:
		return nil
	case <-d.stop:
		return io.EOF
	}
}
func (*testDriver) Normalize([]byte, *packetAddress) error { return nil }
func (d *testDriver) Close()                               { d.once.Do(func() { close(d.stop) }) }

type testSearcher struct{}

func (testSearcher) ResetCache()  {}
func (testSearcher) Close() error { return nil }
func (testSearcher) FindProcessInfo(_ context.Context, _ string, source, destination netip.AddrPort) (*adapter.ConnectionOwner, error) {
	if source.Port() == 41003 {
		return nil, fmt.Errorf("unknown owner")
	}
	name := `C:\Fixture\ignored.exe`
	if source.Port() == 41001 {
		name = `C:\Fixture\selected.exe`
	}
	return &adapter.ConnectionOwner{ProcessID: uint32(source.Port()), ProcessPath: name}, nil
}

type echoRouter struct {
	adapter.Router
	received chan string
}

func (r *echoRouter) RoutePacketConnectionEx(_ context.Context, c N.PacketConn, _ adapter.InboundContext, _ N.CloseHandlerFunc) {
	go func() {
		defer c.Close()
		b := buf.NewPacket()
		destination, err := c.ReadPacket(b)
		if err != nil {
			b.Release()
			return
		}
		r.received <- string(b.Bytes())
		c.WritePacket(b, destination)
	}()
}

func checksum(data []byte) uint16 {
	var sum uint32
	for len(data) > 1 {
		sum += uint32(binary.BigEndian.Uint16(data[:2]))
		data = data[2:]
	}
	if len(data) > 0 {
		sum += uint32(data[0]) << 8
	}
	for sum>>16 != 0 {
		sum = (sum & 65535) + (sum >> 16)
	}
	return ^uint16(sum)
}
func testUDP(ipv6 bool, port uint16, payload string) []byte {
	offset := 20
	if ipv6 {
		offset = 40
	}
	b := make([]byte, offset+8+len(payload))
	binary.BigEndian.PutUint16(b[offset:], port)
	binary.BigEndian.PutUint16(b[offset+2:], 443)
	binary.BigEndian.PutUint16(b[offset+4:], uint16(8+len(payload)))
	copy(b[offset+8:], payload)
	if ipv6 {
		b[0] = 0x60
		b[6] = 17
		b[7] = 64
		binary.BigEndian.PutUint16(b[4:], uint16(len(b)-40))
		source := netip.MustParseAddr("fdfe:7767:7475::2").As16()
		destination := netip.MustParseAddr("2001:db8::1").As16()
		copy(b[8:24], source[:])
		copy(b[24:40], destination[:])
		pseudo := append([]byte(nil), b[8:40]...)
		pseudo = append(pseudo, 0, 0, 0, byte(len(b)-40), 0, 0, 0, 17)
		pseudo = append(pseudo, b[40:]...)
		value := checksum(pseudo)
		if value == 0 {
			value = 65535
		}
		binary.BigEndian.PutUint16(b[46:], value)
	} else {
		b[0] = 0x45
		b[8] = 64
		b[9] = 17
		binary.BigEndian.PutUint16(b[2:], uint16(len(b)))
		copy(b[12:16], []byte{172, 28, 199, 2})
		copy(b[16:20], []byte{198, 51, 100, 1})
		binary.BigEndian.PutUint16(b[10:], checksum(b[:20]))
	}
	return b
}

func TestOnlySelectedPacketsEnterStack(t *testing.T) {
	for _, ipv6 := range []bool{false, true} {
		t.Run(fmt.Sprint("ipv6=", ipv6), func(t *testing.T) {
			p, err := CompileWindows(Export{Final: "direct", Rules: []json.RawMessage{json.RawMessage(`{"process_name":["selected.exe"],"action":"route","outbound":"active"}`)}})
			if err != nil {
				t.Fatal(err)
			}
			d := &testDriver{in: make(chan testPacket, 8), out: make(chan testPacket, 8), stop: make(chan struct{})}
			router := &echoRouter{received: make(chan string, 8)}
			engine, err := createPacketEngine(context.Background(), router, p, func() (packetDriver, error) { return d, nil })
			if err != nil {
				t.Fatal(err)
			}
			defer engine.Close()
			engine.searcher.Close()
			engine.searcher = testSearcher{}
			engine.Start()
			address := packetAddress{}
			address[1] = 1 << 17
			if ipv6 {
				address[1] |= 1 << 20
			}
			for i, name := range []string{"selected", "ignored", "unknown"} {
				d.in <- testPacket{testUDP(ipv6, uint16(41001+i), name), address}
			}
			select {
			case payload := <-router.received:
				if payload != "selected" {
					t.Fatalf("unexpected stack ingress %q", payload)
				}
			case err := <-engine.errors:
				t.Fatal(err)
			case <-time.After(3 * time.Second):
				t.Fatal("selected packet never reached actual sing-tun")
			}
			direct, replies := 0, 0
			for n := 0; n < 3; n++ {
				select {
				case packet := <-d.out:
					parsed, e := parsePacket(packet.data)
					if e != nil {
						t.Fatal(e)
					}
					if packet.address[1]&(1<<17) != 0 {
						direct++
						if parsed.key.source.Port() == 41001 {
							t.Fatal("selected payload bypassed stack")
						}
					} else {
						replies++
						if parsed.key.destination.Port() != 41001 {
							t.Fatal("reply went to excluded socket")
						}
					}
				case err := <-engine.errors:
					t.Fatal(err)
				case <-time.After(3 * time.Second):
					t.Fatal("missing pass-through or reply")
				}
			}
			if direct != 2 || replies != 1 {
				t.Fatalf("direct=%d replies=%d", direct, replies)
			}
			select {
			case payload := <-router.received:
				t.Fatalf("excluded packet entered stack: %s", payload)
			default:
			}
		})
	}
}

func TestIPv6ExtensionAndMalformedPackets(t *testing.T) {
	b := testUDP(true, 41001, "payload")
	extension := make([]byte, len(b)+8)
	copy(extension, b[:40])
	extension[6] = 60
	extension[40] = 17
	copy(extension[48:], b[40:])
	binary.BigEndian.PutUint16(extension[4:], uint16(len(extension)-40))
	if p, err := parsePacket(extension); err != nil || p.key.source.Port() != 41001 {
		t.Fatalf("extension parsing: %+v %v", p, err)
	}
	for _, packet := range [][]byte{nil, b[:39], b[:42], {0x45}, make([]byte, 40)} {
		if _, err := parsePacket(packet); err == nil {
			t.Fatal("malformed packet accepted")
		}
	}
}
