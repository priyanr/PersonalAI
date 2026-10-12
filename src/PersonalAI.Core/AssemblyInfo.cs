using System.Runtime.CompilerServices;

// Lets Sandbox/SandboxHostRegistryTests drive SandboxHostRegistry's connection lifecycle over a
// FakeFrameTransport (the internal RunConnectionAsync test seam) without a real WebSocket.
[assembly: InternalsVisibleTo("PersonalAI.Tests")]
