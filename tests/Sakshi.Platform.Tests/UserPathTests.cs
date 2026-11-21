using Sakshi.Platform.Windows;

namespace Sakshi.Platform.Tests;

public sealed class UserPathTests
{
    [Fact]
    public void Add_appends_the_dir() =>
        Assert.Equal(@"C:\a;C:\b;C:\s", UserPath.Add(@"C:\a;C:\b", @"C:\s"));

    [Fact]
    public void Add_twice_changes_nothing() =>
        Assert.Equal(@"C:\a;C:\b;C:\s", UserPath.Add(@"C:\a;C:\b;C:\s", @"C:\s"));

    [Fact]
    public void Add_ignores_case_and_a_trailing_backslash_when_looking_for_the_dir() =>
        Assert.Equal(@"C:\a;c:\S\", UserPath.Add(@"C:\a;c:\S\", @"C:\s"));

    [Fact]
    public void Add_to_an_empty_value_is_just_the_dir() => Assert.Equal(@"C:\s", UserPath.Add("", @"C:\s"));

    [Fact]
    public void Add_keeps_empty_entries_and_a_trailing_separator()
    {
        const string original = @"C:\a;;C:\b;";
        var added = UserPath.Add(original, @"C:\s");
        Assert.Equal(@"C:\a;;C:\b;C:\s;", added);
        Assert.Equal(original, UserPath.Remove(added, @"C:\s"));
    }

    [Fact]
    public void Remove_drops_the_matching_entry_and_keeps_the_rest_in_order() =>
        Assert.Equal(@"C:\a;C:\b", UserPath.Remove(@"C:\a;c:\S\;C:\b", @"C:\s"));

    [Fact]
    public void Remove_leaves_variables_and_empty_entries_as_written() =>
        Assert.Equal(@"%USERPROFILE%\bin;;C:\b;", UserPath.Remove(@"%USERPROFILE%\bin;;C:\s;C:\b;", @"C:\s"));

    [Fact]
    public void Remove_of_a_dir_that_is_not_there_changes_nothing() =>
        Assert.Equal(@"C:\a;;C:\b;", UserPath.Remove(@"C:\a;;C:\b;", @"C:\s"));

    [Fact]
    public void Remove_does_not_touch_a_longer_path_with_the_same_prefix() =>
        Assert.Equal(@"C:\s2;C:\s\sub", UserPath.Remove(@"C:\s2;C:\s;C:\s\sub", @"C:\s"));

    private sealed class FakeKey(string? value, PathKind? kind) : IEnvironmentKey
    {
        public (string? Value, PathKind? Kind)? Set;
        public (string?, PathKind?) GetPath() => (value, kind);
        public void SetPath(string v, PathKind k) => Set = (v, k);
    }

    private static int Apply(FakeKey key, Func<string, string> change)
    {
        var broadcasts = 0;
        UserPath.Apply(key, change, () => broadcasts++);
        return broadcasts;
    }

    [Fact]
    public void An_absent_path_becomes_an_expandable_value_with_just_our_dir()
    {
        var key = new FakeKey(null, null);
        Assert.Equal(1, Apply(key, p => UserPath.Add(p, @"C:\s")));
        Assert.Equal((@"C:\s", PathKind.ExpandString), key.Set);
    }

    [Fact]
    public void An_unchanged_value_is_not_written_or_broadcast()
    {
        var key = new FakeKey(@"C:\a;C:\s", PathKind.ExpandString);
        Assert.Equal(0, Apply(key, p => UserPath.Add(p, @"C:\s")));
        Assert.Null(key.Set);
    }

    [Fact]
    public void A_value_of_another_kind_is_left_alone()
    {
        var key = new FakeKey(null, PathKind.Other);
        Assert.Throws<InvalidOperationException>(() => Apply(key, p => UserPath.Add(p, @"C:\s")));
        Assert.Null(key.Set);
    }

    [Fact]
    public void A_plain_string_stays_a_plain_string()
    {
        var key = new FakeKey(@"C:\a", PathKind.String);
        Apply(key, p => UserPath.Add(p, @"C:\s"));
        Assert.Equal((@"C:\a;C:\s", PathKind.String), key.Set);
    }

    [Fact]
    public void Uninstall_gives_back_exactly_the_original_value()
    {
        var key = new FakeKey(@"C:\a;;C:\b;", PathKind.ExpandString);
        Apply(key, p => UserPath.Add(p, @"C:\s"));
        var installed = new FakeKey(key.Set!.Value.Value, PathKind.ExpandString);
        Apply(installed, p => UserPath.Remove(p, @"C:\s"));
        Assert.Equal(@"C:\a;;C:\b;", installed.Set!.Value.Value);
    }
}
