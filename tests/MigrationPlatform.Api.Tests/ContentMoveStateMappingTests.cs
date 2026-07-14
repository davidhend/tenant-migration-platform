using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Workers;

namespace MigrationPlatform.Api.Tests;

public class MapMoveStateTests
{
    [Theory]
    [InlineData("Success")]
    [InlineData("Completed")]
    public void Success_states_complete_the_item(string state)
        => Assert.Equal(ContentMigrationItemStatus.Completed, ContentMigrationWorker.MapMoveState(state));

    [Theory]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    public void Failure_states_fail_the_item(string state)
        => Assert.Equal(ContentMigrationItemStatus.Failed, ContentMigrationWorker.MapMoveState(state));

    [Theory]
    [InlineData("Queued")]
    [InlineData("NotStarted")]
    [InlineData("InProgress")]
    [InlineData("ReadyToTrigger")]
    [InlineData("Rescheduled")]
    [InlineData("RescheduleManualTrigger")]
    [InlineData("Scheduled")]
    public void In_flight_states_keep_running(string state)
        => Assert.Equal(ContentMigrationItemStatus.Running, ContentMigrationWorker.MapMoveState(state));

    // Regression guard: a NEW Microsoft-side state must never fail an item —
    // unknown states stay Running (the 72h timeout is the backstop).
    [Theory]
    [InlineData("SomeBrandNewState")]
    [InlineData("")]
    public void Unknown_states_are_treated_as_running_not_failed(string state)
        => Assert.Equal(ContentMigrationItemStatus.Running, ContentMigrationWorker.MapMoveState(state));

    [Fact]
    public void Known_active_states_set_matches_the_mapper()
    {
        foreach (var state in ContentMigrationWorker.KnownActiveStates)
            Assert.Equal(ContentMigrationItemStatus.Running, ContentMigrationWorker.MapMoveState(state));
    }
}
