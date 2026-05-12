// identity.ts
//
// Circle AI identity layer.
// A Circle identity is the unified persona key that travels with the person.
// Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.

/** Verification tier for a CircleIdentity. */
export enum IdentityTier {
  Anonymous    = 'Anonymous',
  Pseudonymous = 'Pseudonymous',
  Verified     = 'Verified',
}

/**
 * A Circle AI identity — the unified persona key that travels with the person.
 */
export interface CircleIdentity {
  /** Stable UUID — never changes. */
  readonly identityId: string;
  readonly displayName: string;
  readonly preferredLanguage: string | null;
  readonly tier: IdentityTier;
  readonly deviceIds: readonly string[];
  readonly createdAt: Date;
  readonly lastSeenAt: Date;
}

/**
 * A device registered to an identity.
 * platform is one of: "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot"
 */
export interface RegisteredDevice {
  readonly deviceId: string;
  readonly identityId: string;
  /** "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot" */
  readonly platform: string;
  readonly deviceName: string | null;
  readonly registeredAt: Date;
  readonly lastActiveAt: Date;
}

/** Persistent store for Circle AI identities and device registrations. */
export abstract class IIdentityStore {
  abstract get(identityId: string): Promise<CircleIdentity | null>;
  abstract save(identity: CircleIdentity): Promise<void>;
  abstract getDevices(identityId: string): Promise<readonly RegisteredDevice[]>;
  abstract registerDevice(device: RegisteredDevice): Promise<void>;
  abstract getByDevice(deviceId: string): Promise<CircleIdentity | null>;
}

/**
 * Resolves the active identity for the current device/session.
 * Implementations may use local storage, biometrics, or mesh-distributed keys.
 */
export abstract class IIdentityProvider {
  abstract getCurrentIdentity(): Promise<CircleIdentity | null>;
  abstract isAuthenticated(): Promise<boolean>;
  abstract createIdentity(displayName: string, preferredLanguage?: string | null): Promise<CircleIdentity>;
}
