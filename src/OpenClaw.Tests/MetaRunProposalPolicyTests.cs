using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MetaRunProposalPolicyTests
{
    [Fact]
    public void IsAllowedActionTransition_Accept_FromPending_ToApproved_ReturnsTrue()
    {
        var allowed = MetaRunProposalPolicy.IsAllowedActionTransition(
            MetaRunProposalActions.Accept,
            LearningProposalStatus.Pending,
            LearningProposalStatus.Approved);

        Assert.True(allowed);
    }

    [Fact]
    public void IsAllowedActionTransition_Accept_FromRolledBack_ToApproved_ReturnsFalse()
    {
        var allowed = MetaRunProposalPolicy.IsAllowedActionTransition(
            MetaRunProposalActions.Accept,
            LearningProposalStatus.RolledBack,
            LearningProposalStatus.Approved);

        Assert.False(allowed);
    }

    [Fact]
    public void IsAllowedActionTransition_Change_FromRolledBack_ToRejected_ReturnsTrue()
    {
        var allowed = MetaRunProposalPolicy.IsAllowedActionTransition(
            MetaRunProposalActions.Change,
            LearningProposalStatus.RolledBack,
            LearningProposalStatus.Rejected);

        Assert.True(allowed);
    }
}
