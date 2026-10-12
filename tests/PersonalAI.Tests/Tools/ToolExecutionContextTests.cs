using FluentAssertions;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Tools;

public class ToolExecutionContextTests
{
    /// <summary>Locks in backward compatibility: every existing `new ToolExecutionContext(false)`
    /// construction across the codebase and test suite (there are dozens) must keep compiling and
    /// keep meaning "no sandbox owner" now that Sandbox was added as a second, optional parameter.</summary>
    [Fact]
    public void DefaultConstruction_SandboxIsNull()
    {
        new ToolExecutionContext(false).Sandbox.Should().BeNull();
    }
}
