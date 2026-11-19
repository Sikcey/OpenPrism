//go:build windows && with_gvisor

package main

import (
	"fmt"
	"golang.org/x/sys/windows"
	"os"
	"sync"
	"unsafe"
)

type packetAddress [10]uint64
type packetDriver interface {
	Read([]byte) (int, packetAddress, error)
	Write([]byte, packetAddress) error
	Normalize([]byte, *packetAddress) error
	Close()
}
type divertDriver struct {
	dll                                   *windows.DLL
	handle                                uintptr
	recv, send, shutdown, close, checksum *windows.Proc
	mu                                    sync.Mutex
	once                                  sync.Once
}

func openDriver(path string) (*divertDriver, error) {
	return openDriverFilter(path, "outbound and !loopback and !impostor and ((tcp and tcp.DstPort != 53) or (udp and udp.DstPort != 53))")
}
func openDriverFilter(path, expression string) (*divertDriver, error) {
	dll, err := windows.LoadDLL(path)
	if err != nil {
		return nil, err
	}
	d := &divertDriver{dll: dll}
	fail := func(e error) (*divertDriver, error) { dll.Release(); return nil, e }
	procedures := []struct {
		name   string
		target **windows.Proc
	}{{"WinDivertRecv", &d.recv}, {"WinDivertSend", &d.send}, {"WinDivertShutdown", &d.shutdown}, {"WinDivertClose", &d.close}, {"WinDivertHelperCalcChecksums", &d.checksum}}
	for _, item := range procedures {
		p, e := dll.FindProc(item.name)
		if e != nil {
			return fail(e)
		}
		*item.target = p
	}
	open, err := dll.FindProc("WinDivertOpen")
	if err != nil {
		return fail(err)
	}
	// Kernel filter never diverts local proxy/engine communication or DNS.
	// Packets from this process are passed through after ownership lookup.
	filter, _ := windows.BytePtrFromString(expression)
	var flags uintptr
	if os.Getenv("WGTUN_CAPTURE_NO_INSTALL") == "1" {
		flags = 0x10
	}
	handle, _, callErr := open.Call(uintptr(unsafe.Pointer(filter)), 0, 100, flags)
	if handle == ^uintptr(0) {
		return fail(fmt.Errorf("WinDivertOpen: %v", callErr))
	}
	d.handle = handle
	return d, nil
}
func (d *divertDriver) Read(b []byte) (int, packetAddress, error) {
	var address packetAddress
	var n uint32
	ok, _, err := d.recv.Call(d.handle, uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)), uintptr(unsafe.Pointer(&n)), uintptr(unsafe.Pointer(&address[0])))
	if ok == 0 {
		return 0, address, fmt.Errorf("WinDivertRecv: %v", err)
	}
	return int(n), address, nil
}
func (d *divertDriver) Write(b []byte, address packetAddress) error {
	if len(b) == 0 {
		return nil
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if err := d.Normalize(b, &address); err != nil {
		return err
	}
	var n uint32
	ok, _, err := d.send.Call(d.handle, uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)), uintptr(unsafe.Pointer(&n)), uintptr(unsafe.Pointer(&address[0])))
	if ok == 0 || int(n) != len(b) {
		return fmt.Errorf("WinDivertSend: %v", err)
	}
	return nil
}
func (d *divertDriver) Normalize(b []byte, address *packetAddress) error {
	ok, _, err := d.checksum.Call(uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)), uintptr(unsafe.Pointer(&address[0])), 0)
	if ok == 0 {
		return fmt.Errorf("packet checksum: %v", err)
	}
	return nil
}
func (d *divertDriver) Close() {
	d.once.Do(func() { d.shutdown.Call(d.handle, 1); d.mu.Lock(); defer d.mu.Unlock(); d.close.Call(d.handle) })
}

// DLL stays loaded until process exit: an outstanding native Recv may still be
// returning when shutdown starts. Closing the handle releases its WFP filters.
