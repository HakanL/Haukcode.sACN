# Changelog

The package version is major.minor from version.json plus the CI run number, so
every push to main publishes a new patch version. Breaking changes bump the
major version.

## 3.0 (2026-10-20) - breaking

Standards compliance pass against ANSI E1.31-2018. See "Upgrading from 2.x" in
the README.

- Breaking: fields that E1.31 defines as uint16/uint32 are now ushort/uint
  (RootLayer lengths and vectors, FramingLayer vectors, DMPLayer address,
  increment and length). A property value count of 0x8000 or more is now
  rejected instead of read as empty data.
- Fixed the Universe Discovery packet layout and multicast address.
- Packets that E1.31 says to discard are discarded, and PDU lengths are
  validated.
- Sending validates the DMX length, source name, universe and priority ranges.
- Stream_Terminated packets are delivered, three are sent on termination, and
  the option bits are exposed.
- Sync packets carry their SubscribeMode.
- Thread safety: SendDmxData can be called from several threads, the endpoint
  caches and subscriptions are thread-safe, a failed constructor no longer
  leaks, and joins are validated.
- ReceiveDataPacket.Clone, for queuing a received packet (the receive path
  reuses the instance).
- The package embeds its PDB with SourceLink, so the debugger can step into
  the library source (3.0.118 and later).

## 2.0 (2021-02-02)

Later 2.0.x releases:

- .NET 9 and .NET 10 support.
- Rewritten on the Haukcode.HighPerfComm library, with separate send and
  receive sockets.
- Multicast TTL 20.
- Triggers, and the subscribe mode on received packets.
- Universe Discovery packet, with an option to join and drop the discovery
  multicast group.
- A send-immediately method.
- Kernel receive timestamps on Linux, and IGMP memberships held on a separate
  socket on Linux.
- Performance: sending sharded by universe, no per-packet allocations on the
  send and receive paths, lock-free sequence numbers, sync packets routed
  through the ordering barrier.
- 2.0.16 - Fix incorrect byte order of Acn Source Id
- 2.0.13 - Fix issue with ReceiveMessageFrom and reusing the socket events
- 2.0.0 - Allow for sync packets, breaking change

## 1.x

- 1.0.8 - Allow unknown operational status on network adapter
- 1.0.7 - Make it Linux-compatible
- 1.0.6 - Removed unused dependency
- 1.0.5 - Added method to list network adapters
- 1.0.4 - Changed API for GetAddressesFromInterfaceType
- 1.0.3 - Changed DMXData property
- 1.0.2 - Added method for multi-universe listener
- 1.0.1 - Added methods to find local IPAddress
- 1.0.0 - Refactored
