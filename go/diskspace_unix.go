//go:build !windows

// diskspace_unix.go
//
// Unix (Linux/macOS) free-disk-space probe backing
// ModelDownloadService.GetAvailableDiskSpaceBytes. Uses statfs via syscall so
// no third-party dependency is pulled in (only github.com/google/uuid is
// permitted).

package circleai

import "syscall"

func availableDiskSpaceBytes(path string) (int64, error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return 0, err
	}
	// Bavail = free blocks available to unprivileged users; Bsize = block size.
	return int64(st.Bavail) * int64(st.Bsize), nil
}
