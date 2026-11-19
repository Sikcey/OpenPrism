//go:build windows

package main

import (
	"context"
	"fmt"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing/common/winiphlpapi"
	"golang.org/x/sys/windows"
	"net/netip"
	"sync"
	"time"
)

type cachedProcess struct {
	handle windows.Handle
	owner  *adapter.ConnectionOwner
	used   time.Time
}
type nativeSearcher struct {
	mu     sync.Mutex
	cache  map[uint32]*cachedProcess
	closed bool
}

func (s *nativeSearcher) ResetCache() {}
func (s *nativeSearcher) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
	for pid, entry := range s.cache {
		windows.CloseHandle(entry.handle)
		delete(s.cache, pid)
	}
	return nil
}

// Match the entire TCP tuple and refuse ambiguous UDP shared bindings.
func endpointPID(network string, source, destination netip.AddrPort) (uint32, error) {
	matches := 0
	var pid uint32
	add := func(value uint32) { matches++; pid = value }
	if network == "tcp" && source.Addr().Is4() {
		rows, err := winiphlpapi.GetExtendedTcpTable()
		if err != nil {
			return 0, err
		}
		for _, r := range rows {
			if source == netip.AddrPortFrom(winiphlpapi.DwordToAddr(r.DwLocalAddr), winiphlpapi.DwordToPort(r.DwLocalPort)) && destination == netip.AddrPortFrom(winiphlpapi.DwordToAddr(r.DwRemoteAddr), winiphlpapi.DwordToPort(r.DwRemotePort)) {
				add(r.DwOwningPid)
			}
		}
	} else if network == "tcp" {
		rows, err := winiphlpapi.GetExtendedTcp6Table()
		if err != nil {
			return 0, err
		}
		for _, r := range rows {
			if source == netip.AddrPortFrom(netip.AddrFrom16(r.UcLocalAddr), winiphlpapi.DwordToPort(r.DwLocalPort)) && destination == netip.AddrPortFrom(netip.AddrFrom16(r.UcRemoteAddr), winiphlpapi.DwordToPort(r.DwRemotePort)) {
				add(r.DwOwningPid)
			}
		}
	} else if source.Addr().Is4() {
		rows, err := winiphlpapi.GetExtendedUdpTable()
		if err != nil {
			return 0, err
		}
		for _, r := range rows {
			ip := winiphlpapi.DwordToAddr(r.DwLocalAddr)
			if source.Port() == winiphlpapi.DwordToPort(r.DwLocalPort) && (ip == source.Addr() || ip.IsUnspecified()) {
				add(r.DwOwningPid)
			}
		}
	} else {
		rows, err := winiphlpapi.GetExtendedUdp6Table()
		if err != nil {
			return 0, err
		}
		for _, r := range rows {
			ip := netip.AddrFrom16(r.UcLocalAddr)
			if source.Port() == winiphlpapi.DwordToPort(r.DwLocalPort) && (ip == source.Addr() || ip.IsUnspecified()) {
				add(r.DwOwningPid)
			}
		}
	}
	if matches != 1 || pid == 0 {
		return 0, fmt.Errorf("socket ownership absent or ambiguous")
	}
	return pid, nil
}
func (s *nativeSearcher) FindProcessInfo(ctx context.Context, network string, source, destination netip.AddrPort) (*adapter.ConnectionOwner, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	pid, err := endpointPID(network, source, destination)
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return nil, fmt.Errorf("owner searcher closed")
	}
	if s.cache == nil {
		s.cache = make(map[uint32]*cachedProcess)
	}
	if entry := s.cache[pid]; entry != nil {
		status, e := windows.WaitForSingleObject(entry.handle, 0)
		if e == nil && status == uint32(windows.WAIT_TIMEOUT) {
			entry.used = time.Now()
			return entry.owner, nil
		}
		windows.CloseHandle(entry.handle)
		delete(s.cache, pid)
	}
	handle, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION|windows.SYNCHRONIZE, false, pid)
	if err != nil {
		return nil, err
	}
	retained := false
	defer func() {
		if !retained {
			windows.CloseHandle(handle)
		}
	}()
	name := make([]uint16, 32768)
	size := uint32(len(name))
	if err = windows.QueryFullProcessImageName(handle, 0, &name[0], &size); err != nil {
		return nil, err
	}
	second, err := endpointPID(network, source, destination)
	if err != nil || second != pid {
		return nil, fmt.Errorf("socket owner changed")
	}
	status, err := windows.WaitForSingleObject(handle, 0)
	if err != nil || status != uint32(windows.WAIT_TIMEOUT) {
		return nil, fmt.Errorf("owner exited")
	}
	owner := &adapter.ConnectionOwner{ProcessID: pid, ProcessPath: windows.UTF16ToString(name[:size]), UserId: -1}
	if len(s.cache) >= 1024 {
		var oldestPID uint32
		var oldest time.Time
		for id, entry := range s.cache {
			if oldest.IsZero() || entry.used.Before(oldest) {
				oldest = entry.used
				oldestPID = id
			}
		}
		windows.CloseHandle(s.cache[oldestPID].handle)
		delete(s.cache, oldestPID)
	}
	s.cache[pid] = &cachedProcess{handle: handle, owner: owner, used: time.Now()}
	retained = true
	return owner, nil
}
