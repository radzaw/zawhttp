namespace ZawHTTP.Tests.Unit;

public class VariablesTests
{
    [Fact]
    public void Resolves_variables_with_later_entries_winning_and_ignores_disabled_ones()
    {
        var v = new Variables([
            new KeyValue("host", "global.example"),
            new KeyValue("token", "g-token"),
            new KeyValue("host", "dev.example"),
            new KeyValue("token", "off", enabled: false),
        ]);
        Assert.Equal("https://dev.example/api?t=g-token", v.Apply("https://{{host}}/api?t={{ token }}"));
        Assert.Empty(v.Unresolved);
    }

    [Fact]
    public void Values_may_refer_to_other_variables()
    {
        var v = new Variables([new KeyValue("base", "https://{{host}}/v{{ver}}"), new KeyValue("host", "api.example"), new KeyValue("ver", "2")]);
        Assert.Equal("https://api.example/v2/users", v.Apply("{{base}}/users"));
    }

    [Fact]
    public void Unknown_variables_stay_as_typed_and_are_reported()
    {
        var v = new Variables([new KeyValue("a", "1")]);
        Assert.Equal("1-{{missing}}", v.Apply("{{a}}-{{missing}}"));
        Assert.Equal(["missing"], v.Unresolved);
    }

    [Fact]
    public void A_self_reference_does_not_loop_forever()
    {
        var v = new Variables([new KeyValue("x", "{{x}}!")]);
        Assert.StartsWith("{{x}}", v.Apply("{{x}}"));
    }

    [Fact]
    public void Dynamic_variables()
    {
        var v = new Variables(null);
        Assert.True(Guid.TryParse(v.Apply("{{$guid}}"), out _));
        Assert.True(long.TryParse(v.Apply("{{$timestamp}}"), out var ts) && ts > 1_700_000_000);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", v.Apply("{{$isoTimestamp}}"));
        Assert.InRange(int.Parse(v.Apply("{{$randomInt}}")), 0, 1000);
        Assert.Empty(v.Unresolved);
    }

    [Fact]
    public void Text_without_placeholders_is_returned_unchanged()
    {
        var v = new Variables([new KeyValue("a", "1")]);
        Assert.Equal("{ \"json\": {\"nested\": 1} }", v.Apply("{ \"json\": {\"nested\": 1} }"));
        Assert.Equal("", v.Apply(null));
    }
}
