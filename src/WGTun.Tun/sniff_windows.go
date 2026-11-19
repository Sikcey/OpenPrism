//go:build windows && with_gvisor

package main

import (
	"bytes"
	"context"
	"errors"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/common/sniff"
	M "github.com/sagernet/sing/common/metadata"
	"io"
	"net"
	"time"
)

type replayConn struct {
	net.Conn
	reader io.Reader
}

func (c *replayConn) Read(b []byte) (int, error) { return c.reader.Read(b) }

// This re-resolves only selected TCP connections with a readable TLS/HTTP host.
// It never claims to intercept/encrypt the application's original Windows DNS.
func sniffDestination(ctx context.Context, c net.Conn, metadata adapter.InboundContext) (net.Conn, adapter.InboundContext) {
	if sniff.Skip(&metadata) {
		return c, metadata
	}
	c.SetReadDeadline(time.Now().Add(750 * time.Millisecond))
	defer c.SetReadDeadline(time.Time{})
	data := make([]byte, 0, 65536)
	chunk := make([]byte, 4096)
	for len(data) < 65536 {
		limit := len(chunk)
		if limit > 65536-len(data) {
			limit = 65536 - len(data)
		}
		n, err := c.Read(chunk[:limit])
		data = append(data, chunk[:n]...)
		if len(data) > 0 {
			attempt := metadata
			var parseErr error
			if data[0] == 22 {
				parseErr = sniff.TLSClientHello(ctx, &attempt, bytes.NewReader(data))
			} else {
				if data[0] < 'A' || data[0] > 'Z' {
					parseErr = io.EOF
				} else if !bytes.Contains(data, []byte("\r\n\r\n")) {
					parseErr = sniff.ErrNeedMoreData
				} else {
					parseErr = sniff.HTTPHost(ctx, &attempt, bytes.NewReader(data))
				}
			}
			if parseErr == nil && attempt.Domain != "" {
				metadata.Domain = attempt.Domain
				metadata.Protocol = attempt.Protocol
				metadata.Destination = M.ParseSocksaddrHostPort(attempt.Domain, metadata.Destination.Port)
				break
			}
			if parseErr != nil && !errors.Is(parseErr, sniff.ErrNeedMoreData) {
				break
			}
		}
		if err != nil {
			break
		}
	}
	if len(data) == 0 {
		return c, metadata
	}
	return &replayConn{Conn: c, reader: io.MultiReader(bytes.NewReader(data), c)}, metadata
}
