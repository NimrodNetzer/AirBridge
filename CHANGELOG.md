# Changelog

All notable changes to AirBridge will be documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

### Added
- Project scaffold: Windows solution (5 C# projects) and Android Gradle project
- Core interfaces on both platforms: `IDeviceRegistry`, `ITransferSession`, `IMirrorSession`, `IPairingService`
- Transport interfaces: `IDiscoveryService`, `IConnectionManager`, `IMessageChannel`
- Protocol v1 specification and Protobuf definitions
- GitHub Actions CI for Windows (.NET) and Android (Gradle)
- Branch strategy: `main`, `dev`, and 5 feature branches
