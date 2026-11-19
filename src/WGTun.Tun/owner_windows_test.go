//go:build windows

package main

import (
	"context"
	"github.com/sagernet/sing/common/winiphlpapi"
	"net"
	"net/netip"
	"os"
	"strings"
	"testing"
)

func TestRealOwnerCacheRetainsProcessIdentity(t *testing.T) {
	if err := winiphlpapi.LoadExtendedTable(); err != nil {
		t.Fatal(err)
	}
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer socket.Close()
	source := socket.LocalAddr().(*net.UDPAddr).AddrPort()
	source = netip.AddrPortFrom(source.Addr().Unmap(), source.Port())
	searcher := &nativeSearcher{}
	defer searcher.Close()
	first, err := searcher.FindProcessInfo(context.Background(), "udp", source, source)
	if err != nil {
		t.Fatal(err)
	}
	second, err := searcher.FindProcessInfo(context.Background(), "udp", source, source)
	if err != nil {
		t.Fatal(err)
	}
	executable, _ := os.Executable()
	if first != second || first.ProcessID != uint32(os.Getpid()) || !strings.EqualFold(first.ProcessPath, executable) || len(searcher.cache) != 1 {
		t.Fatal("real process identity was not reused safely")
	}
	searcher.Close()
	if _, err = searcher.FindProcessInfo(context.Background(), "udp", source, source); err == nil {
		t.Fatal("closed process cache accepted a lookup")
	}
}
