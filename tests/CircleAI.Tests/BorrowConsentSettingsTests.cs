// BorrowConsentSettingsTests.cs
//
// The borrow consent the settings screen persists, and its mapping to the product's
// OffloadConsent. The shape and the rule live in the product (AppSettings.Borrowing);
// the thin-client screen only renders and stores these two fields.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class BorrowConsentSettingsTests
{
    [Fact]
    public void Off_by_default()
    {
        var s = new AppSettings();
        Assert.False(s.BorrowEnabled);
        Assert.Null(s.BorrowNodeId);
        Assert.False(s.Borrowing.CanBorrow);
    }

    [Fact]
    public void On_with_a_named_node_can_borrow()
    {
        var s = new AppSettings(BorrowEnabled: true, BorrowNodeId: "KXJB7-MN2P4");
        Assert.True(s.Borrowing.CanBorrow);
        Assert.Equal("KXJB7-MN2P4", s.Borrowing.ChosenNodeId);
    }

    [Theory]
    [InlineData(false, "KXJB7-MN2P4")]  // off, even with a node
    [InlineData(true, null)]            // on, but no node named
    [InlineData(true, "   ")]           // on, but a blank node
    public void Does_not_borrow_without_both_on_and_a_node(bool enabled, string? node)
        => Assert.False(new AppSettings(BorrowEnabled: enabled, BorrowNodeId: node).Borrowing.CanBorrow);
}
