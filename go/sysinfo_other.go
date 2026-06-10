//go:build !linux

package circleai

func sysinfoFreeRAM() int64 {
	// Stdlib has no portable way to read free RAM on Windows/Darwin.
	// Callers who want accurate RAM can implement IDeviceContext themselves.
	return 0
}

func probeStorageFree(path string) int64 {
	// Stdlib has no portable Statfs on Windows. Callers who want disk-free
	// info can implement IDeviceContext.StorageFreeBytes themselves.
	_ = path
	return 0
}
