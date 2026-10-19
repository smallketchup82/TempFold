// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Text.Json;
using System.Text.Json.Nodes;
using TempFold.Integrations;
using TempFold.Models;

namespace TempFold.Tests;

public sealed class FahStateTests
{
    [Fact]
    public void EmptyStateHasNoStatusAndMatchesNeitherIntent()
    {
        var state = new FahState();

        Assert.Null(state.Status);
        Assert.False(state.Matches(true));
        Assert.False(state.Matches(false));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"groups\":{}}")]
    [InlineData("{\"groups\":{\"gpu\":{}}}")]
    public void RejectsInitialStateWithoutConfiguration(string json) =>
        Assert.Throws<JsonException>(() => create(json));

    [Theory]
    [InlineData(
        false,
        false,
        true,
        false)]
    [InlineData(
        true,
        false,
        false,
        true)]
    [InlineData(
        false,
        true,
        false,
        false)]
    [InlineData(
        true,
        true,
        false,
        true)]
    public void DistinguishesFoldingPausedAndFinishing(
        bool paused,
        bool finish,
        bool folds,
        bool matchesPause)
    {
        var config = new JsonObject { ["paused"] = paused, ["finish"] = finish };
        var state = new FahState();

        state.Apply(new JsonObject { ["config"] = config }, initial: true);

        Assert.Equal(new FoldingStatus(folds, false), state.Status);
        Assert.Equal(folds, state.Matches(true));
        Assert.Equal(matchesPause, state.Matches(false));
    }

    [Fact]
    public void DefaultFlagsAndLegacyRootConfigurationAreSupported()
    {
        FahState state = create("""{"config":{},"units":[{"state":"RUN"}]}""");

        Assert.Equal(new FoldingStatus(true, true), state.Status);
        Assert.True(state.Matches(true));
    }

    [Fact]
    public void AllGroupsMustConfirmIntentButOneCanBeConfiguredToFold()
    {
        FahState state = create("""{"groups":{"":{"config":{"paused":false}},"gpu":{"config":{"paused":true}}}}""");

        Assert.True(state.Status!.IsConfiguredToFold);
        Assert.False(state.Matches(true));
        Assert.False(state.Matches(false));

        state.Apply(JsonNode.Parse("""["groups","gpu","config","paused",false]"""));

        Assert.True(state.Matches(true));

        state.Apply(JsonNode.Parse("""["groups","gpu",null]"""));

        Assert.True(state.Matches(true));
    }

    [Theory]
    [InlineData(
        "RUN",
        false,
        "",
        true)]
    [InlineData(
        "READY",
        false,
        "",
        false)]
    [InlineData(
        "RUN",
        true,
        "",
        false)]
    [InlineData(
        "RUN",
        false,
        "unknown",
        false)]
    public void WorkingRequiresRunningUnpausedUnitInConfiguredGroup(
        string unitState,
        bool paused,
        string group,
        bool expected)
    {
        var state = new FahState();

        state.Apply(
            new JsonObject
            {
                ["config"] = new JsonObject(),
                ["units"] = new JsonArray(
                    new JsonObject { ["state"] = unitState, ["paused"] = paused, ["group"] = group })
            },
            initial: true);

        Assert.Equal(expected, state.Status!.IsWorking);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("finish")]
    public void RunningUnitDoesNotWorkWhenItsGroupCannotFold(string flag)
    {
        var state = new FahState();

        state.Apply(
            new JsonObject
            {
                ["config"] = new JsonObject { [flag] = true },
                ["units"] = new JsonArray(new JsonObject { ["state"] = "RUN" })
            },
            initial: true);

        Assert.False(state.Status!.IsWorking);
    }

    [Fact]
    public void AppliesAppendConcatNestedUpdateAndDeletion()
    {
        FahState state = create("""{"config":{},"units":[]}""");

        state.Apply(JsonNode.Parse("""["units",-1,{"state":"READY"}]"""));
        state.Apply(JsonNode.Parse("""["units",-2,[{"state":"RUN"},null]]"""));

        Assert.True(state.Status!.IsWorking);

        state.Apply(JsonNode.Parse("""["units",1,null]"""));

        Assert.False(state.Status!.IsWorking);

        state.Apply(JsonNode.Parse("""["units",0,"state","RUN"]"""));

        Assert.True(state.Status!.IsWorking);

        state.Apply(JsonNode.Parse("""["units",0,null]"""));

        Assert.False(state.Status!.IsWorking);
    }

    [Fact]
    public void CreatesMissingObjectAndArrayPathsAndPadsSparseArrays()
    {
        FahState state = create("""{"config":{}}""");

        state.Apply(JsonNode.Parse("""["units",2,"state","RUN"]"""));

        Assert.True(state.Status!.IsWorking);

        state.Apply(JsonNode.Parse("""["units",20,null]"""));

        Assert.True(state.Status!.IsWorking);

        state.Apply(JsonNode.Parse("""["config","paused",true]"""));

        Assert.True(state.Matches(false));
    }

    [Fact]
    public void PatchValuesAreClonedAndCanBeAppliedAgain()
    {
        FahState state = create("""{"config":{},"units":[]}""");
        JsonNode patch = JsonNode.Parse("""["units",-1,{"state":"RUN"}]""")!;

        state.Apply(patch);
        patch[2]!["state"] = "READY";

        Assert.True(state.Status!.IsWorking);

        state.Apply(patch);

        Assert.True(state.Status!.IsWorking);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"ping\"")]
    [InlineData("{}")]
    public void IgnoresNonPatchKeepAlives(string json)
    {
        FahState state = create("""{"config":{}}""");

        state.Apply(JsonNode.Parse(json));

        Assert.True(state.Matches(true));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"units\"]")]
    [InlineData("[\"units\",-3,{}]")]
    [InlineData("[\"units\",-2,{}]")]
    [InlineData("[\"units\",100001,{}]")]
    [InlineData("[\"config\",\"paused\",\"child\",true]")]
    public void RejectsMalformedOrExcessiveUpdates(string patch)
    {
        FahState state = create("""{"config":{"paused":false},"units":[]}""");

        Assert.Throws<JsonException>(() => state.Apply(JsonNode.Parse(patch)));
    }

    [Fact]
    public void AddingPausedGroupPreventsFoldingConfirmationUntilGroupIsRemoved()
    {
        FahState state = create("""{"groups":{"":{"config":{"paused":false}}}}""");

        Assert.True(state.Matches(true));

        state.Apply(JsonNode.Parse("""["groups","new","config",{"paused":true}]"""));

        Assert.False(state.Matches(true));

        state.Apply(JsonNode.Parse("""["groups","new",null]"""));

        Assert.True(state.Matches(true));
    }

    [Fact]
    public void NewSnapshotReplacesOldState()
    {
        FahState state = create("""{"config":{},"units":[{"state":"RUN"}]}""");

        state.Apply(JsonNode.Parse("""{"config":{"paused":true}}"""), initial: true);

        Assert.Equal(new FoldingStatus(false, false), state.Status);
    }

    private static FahState create(string json)
    {
        var state = new FahState();
        state.Apply(JsonNode.Parse(json), initial: true);
        return state;
    }
}