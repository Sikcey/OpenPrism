//go:build windows && with_gvisor

package main

import (
	"context"
	"crypto/tls"
	"github.com/sagernet/sing-box/adapter"
	M "github.com/sagernet/sing/common/metadata"
	"io"
	"net"
	"testing"
	"time"
)

func TestHTTPInspectionReplaysFragmentedBytes(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	payload := "GET / HTTP/1.1\r\nHost: fixture.example\r\n\r\nBODY"
	go func() { client.Write([]byte(payload[:20])); client.Write([]byte(payload[20:])) }()
	original := adapter.InboundContext{Destination: M.ParseSocksaddr("198.51.100.1:443")}
	replay, metadata := sniffDestination(context.Background(), server, original)
	if metadata.Destination.Fqdn != "fixture.example" || metadata.Destination.Port != 443 {
		t.Fatalf("host inspection: %+v", metadata)
	}
	b := make([]byte, len(payload))
	replay.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := io.ReadFull(replay, b); err != nil || string(b) != payload {
		t.Fatalf("replay changed bytes: %q %v", b, err)
	}
}
func TestTLSInspectionRecoversClientHello(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	done := make(chan struct{})
	go func() {
		defer close(done)
		connection := tls.Client(client, &tls.Config{ServerName: "fixture.example", MinVersion: tls.VersionTLS12})
		connection.Handshake()
	}()
	replay, metadata := sniffDestination(context.Background(), server, adapter.InboundContext{Destination: M.ParseSocksaddr("198.51.100.1:443")})
	if metadata.Domain != "fixture.example" || metadata.Destination.Fqdn != "fixture.example" {
		t.Fatalf("TLS SNI: %+v", metadata)
	}
	b := make([]byte, 5)
	if _, err := io.ReadFull(replay, b); err != nil || b[0] != 22 {
		t.Fatalf("ClientHello was not replayed: %x %v", b, err)
	}
	server.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("TLS probe did not stop")
	}
}
func TestServerFirstInspectionIsBounded(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	original := adapter.InboundContext{Destination: M.ParseSocksaddr("198.51.100.1:443")}
	start := time.Now()
	_, metadata := sniffDestination(context.Background(), server, original)
	if metadata.Destination != original.Destination || time.Since(start) > 1500*time.Millisecond {
		t.Fatal("server-first fallback changed destination or exceeded timeout")
	}
}
