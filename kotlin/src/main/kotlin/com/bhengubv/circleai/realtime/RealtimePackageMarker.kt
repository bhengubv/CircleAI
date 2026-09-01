// RealtimePackageMarker.kt
//
// A marker type so a host can resolve this package's resources and confirm it is
// actually on the classpath.
//
// It exists because "the realtime package is missing" and "the realtime package
// is present and misconfigured" are indistinguishable from a caller otherwise,
// and the first is a build problem while the second is a wiring one.
//
// Ported from src/CircleAI.Realtime/RealtimePackageMarker.cs.

package com.bhengubv.circleai.realtime

object RealtimePackageMarker {
    const val PACKAGE_NAME = "com.bhengubv.circleai.realtime"

    /** Resolves to this package's own loader, which is what a host needs to read
     *  resources bundled beside it. */
    val classLoader: ClassLoader get() = RealtimePackageMarker::class.java.classLoader
}
