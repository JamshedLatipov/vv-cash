using VvCash.Models;
using Xunit;

namespace VvCash.Tests;

public class QueueOrderStateTest
{
    [Theory]
    [InlineData(QueueOrderState.New, QueueOrderState.InProgress)]
    [InlineData(QueueOrderState.InProgress, QueueOrderState.Ready)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.Closed)]
    [InlineData(QueueOrderState.New, QueueOrderState.Cancelled)]
    [InlineData(QueueOrderState.InProgress, QueueOrderState.Cancelled)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.Cancelled)]
    public void AllowedTransitions(QueueOrderState from, QueueOrderState to)
        => Assert.True(QueueOrderStates.CanMove(from, to));

    [Theory]
    [InlineData(QueueOrderState.Closed, QueueOrderState.Ready)]
    [InlineData(QueueOrderState.Cancelled, QueueOrderState.New)]
    [InlineData(QueueOrderState.New, QueueOrderState.Closed)]
    [InlineData(QueueOrderState.Ready, QueueOrderState.New)]
    [InlineData(QueueOrderState.Closed, QueueOrderState.Closed)]
    public void RejectedTransitions(QueueOrderState from, QueueOrderState to)
        => Assert.False(QueueOrderStates.CanMove(from, to));
}
