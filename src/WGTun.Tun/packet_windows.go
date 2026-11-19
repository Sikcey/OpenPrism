//go:build windows && with_gvisor

package main

import (
	"encoding/binary"
	"fmt"
	"net/netip"
)

type flowKey struct {
	source, destination netip.AddrPort
	protocol            uint8
}

func (k flowKey) reverse() flowKey { return flowKey{k.destination, k.source, k.protocol} }

type packetInfo struct {
	key flowKey
	syn bool
}

func parsePacket(b []byte) (packetInfo, error) {
	var p packetInfo
	var source, destination netip.Addr
	var next uint8
	offset := 0
	if len(b) < 20 {
		return p, fmt.Errorf("short IP packet")
	}
	switch b[0] >> 4 {
	case 4:
		offset = int(b[0]&15) * 4
		if offset < 20 || len(b) < offset || binary.BigEndian.Uint16(b[6:8])&0x3fff != 0 {
			return p, fmt.Errorf("fragment or invalid IPv4 packet")
		}
		source = netip.AddrFrom4([4]byte(b[12:16]))
		destination = netip.AddrFrom4([4]byte(b[16:20]))
		next = b[9]
	case 6:
		if len(b) < 40 {
			return p, fmt.Errorf("short IPv6 packet")
		}
		source = netip.AddrFrom16([16]byte(b[8:24]))
		destination = netip.AddrFrom16([16]byte(b[24:40]))
		next = b[6]
		offset = 40
		for n := 0; next != 6 && next != 17; n++ {
			if n >= 8 || offset+2 > len(b) {
				return p, fmt.Errorf("invalid IPv6 extension chain")
			}
			extension := next
			next = b[offset]
			switch extension {
			case 0, 43, 60:
				offset += (int(b[offset+1]) + 1) * 8
			case 51:
				offset += (int(b[offset+1]) + 2) * 4
			default:
				return p, fmt.Errorf("unsupported IPv6 next header %d", extension)
			}
		}
	default:
		return p, fmt.Errorf("not IP")
	}
	if next != 6 && next != 17 {
		return p, fmt.Errorf("not TCP or UDP")
	}
	minimum := 8
	if next == 6 {
		minimum = 20
	}
	if offset+minimum > len(b) {
		return p, fmt.Errorf("short transport header")
	}
	p.key = flowKey{netip.AddrPortFrom(source, binary.BigEndian.Uint16(b[offset:offset+2])), netip.AddrPortFrom(destination, binary.BigEndian.Uint16(b[offset+2:offset+4])), next}
	p.syn = next == 6 && b[offset+13]&0x12 == 2
	return p, nil
}
