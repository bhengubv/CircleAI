//go:build windows

// diskspace_windows.go
//
// Windows free-disk-space probe backing
// ModelDownloadService.GetAvailableDiskSpaceBytes. Uses GetDiskFreeSpaceExW
// via syscall so no third-party dependency is pulled in (only
// github.com/google/uuid is permitted).

package circleai

import (
	"path/filepath"
	"syscall"
	"unsafe"
)

func availableDiskSpaceBytes(path string) (int64, error) {
	root := filepath.VolumeName(path)
	if root == "" {
		root = path
	}
	// GetDiskFreeSpaceExW wants a directory path; the volume root works.
	dirPath := root + `\`

	kernel32 := syscall.NewLazyDLL("kernel32.dll")
	proc := kernel32.NewProc("GetDiskFreeSpaceExW")

	p, err := syscall.UTF16PtrFromString(dirPath)
	if err != nil {
		return 0, err
	}
	var freeBytesAvailable uint64
	var totalBytes uint64
	var totalFreeBytes uint64
	r1, _, callErr := proc.Call(
		uintptr(unsafe.Pointer(p)),
		uintptr(unsafe.Pointer(&freeBytesAvailable)),
		uintptr(unsafe.Pointer(&totalBytes)),
		uintptr(unsafe.Pointer(&totalFreeBytes)),
	)
	if r1 == 0 {
		return 0, callErr
	}
	return int64(freeBytesAvailable), nil
}
