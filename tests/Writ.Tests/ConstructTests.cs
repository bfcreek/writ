using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class ConstructTests
{
    [Fact]
    public void Expressions_and_loops_run()
    {
        var value = Run("""
            To Check, returning an integer
                the score is 0
                if "Ada" is like "A*"
                    increase the score
                if "Ada" is not like "B*"
                    increase the score
                if "abc" matches "a.*"
                    increase the score
                if "abc" does not match "z"
                    increase the score
                if (a list containing 1 and 2) contains 1
                    increase the score
                if (a list containing 1 and 2) does not contain 9
                    increase the score
                if 2 is in a list containing 1 and 2
                    increase the score
                if 9 is not in a list containing 1 and 2
                    increase the score
                if nothing is nothing
                    increase the score
                if 1 is not nothing
                    increase the score
                if "4" is a string
                    increase the score
                if 4 is an integer
                    increase the score
                if 1 is equal to 1 and then 2 is equal to 2
                    increase the score
                if 1 is equal to 1 xor 1 is equal to 2
                    increase the score
                if (1 when 2 is equal to 2 otherwise 0) is equal to 1
                    increase the score
                if (nothing otherwise 7) is equal to 7
                    increase the score
                if (6 bitwise and 3) is equal to 2
                    increase the score
                if (1 shifted left by 3) is equal to 8
                    increase the score
                if (bitwise not 0) is equal to -1
                    increase the score
                if (the count of (1 through 3)) is equal to 3
                    increase the score
                if ("ab" times 3) is equal to "ababab"
                    increase the score
                if (the length of ("a,b" split on ",")) is equal to 2
                    increase the score
                the letters are a list containing "a" and "b"
                if (the letters joined by "-") is equal to "a-b"
                    increase the score
                if (the item at 0 of a list containing 4 and 5) is equal to 4
                    increase the score
                if (the last item of a list containing 4 and 5) is equal to 5
                    increase the score
                if (the count of (the items from 0 to 1 of a list containing 1, 2 and 3)) is equal to 2
                    increase the score
                if ("ab" calling ToUpper) is equal to "AB"
                    increase the score
                if (the static IsNullOrEmpty of String with "") is equal to true
                    increase the score
                if ("4" as an integer if possible) is equal to 4
                    increase the score
                if (the count of (1 through 3 piped to a block of the pipeline item)) is equal to 3
                    increase the score
                the person is a table of name set to "Ada" and age set to 4
                if the age of the person is equal to 4
                    increase the score
                the left is a list containing 1 and 2
                the right is a list containing 3
                if (the count of (the left plus the right)) is equal to 3
                    increase the score
                if 1 kb is equal to 1024
                    increase the score
                if 2kb is equal to 2048
                    increase the score
                the total is 0
                for the i from 1 to 3
                    increase the total by the i
                if the total is equal to 6
                    increase the score
                the n is 0
                repeat
                    increase the n
                until the n is equal to 3
                if the n is equal to 3
                    increase the score
                the hops is 0
                while the hops is less than 5
                    increase the hops
                    if the hops is equal to 2
                        continue
                    increase the score
                switch on "b"
                    when "a"
                        increase the score
                    when "b" or "c"
                        increase the score
                    otherwise
                        increase the score
                the jumped is 0
                mark outer
                while the jumped is less than 10
                    increase the jumped
                    if the jumped is equal to 3
                        break to outer
                if the jumped is equal to 3
                    increase the score
                the global mark is 9
                if the global mark is equal to 9
                    increase the score
                redirect the output of "hello" to nothing
                if whether it succeeded
                    increase the score
                return the score
            """, "Check");

        Assert.Equal(44L, value);
    }

    [Fact]
    public void Character_call_and_here_string_run()
    {
        var text = RunText("""
            <# a block comment #>
            To Note, returning a string
                return the character "A" calling ToString
            """, "Note");
        Assert.Equal("A", text);

        var hello = RunText("To Note, returning a string\n    return @\"\nhello\n\"@\n", "Note");
        Assert.Equal("hello", hello);
    }

    [Fact]
    public void Import_module_runs()
    {
        var value = Run("""
            Allow running scripts
            To Load, returning an integer
                import the module "Microsoft.PowerShell.Utility"
                return 1
            """, "Load");
        Assert.Equal(1L, value);
    }

    [Fact]
    public void Readonly_runs()
    {
        var value = Run("""
            To Fixed, returning an integer
                the answer is fixed at 3
                return the answer
            """, "Fixed");
        Assert.Equal(3L, value);
    }

    [Fact]
    public void Generated_csharp_contains_the_new_forms()
    {
        var script = Script("""
            using the namespace System.Text
            export the function Hello
            An enumeration Color of red, green and blue
            An Widget has
                a value which is an integer
                To Double, returning an integer
                    return the value times 2
            To filter Keep, given n which is an integer, returning a boolean
                return the n is equal to 1
            To command Bound, given a name which is a string defaulting to "Ada" from the pipeline also called title one of "Ada" and "Bea" matching "^A" with length from 1 to 4 allowing nothing rejecting empty, and a total which is an integer from 1 to 9 passing the test the total is greater than 0, returning nothing
                before
                    the ready is "yes"
                on each
                    the seen is the name
                after
                    the done is "yes"
                when binding parameters
                    the extra is "yes"
            To Shapes, given a map which is a dictionary of string to integer and cells which are a grid of integer and a slot which is a reference to integer, returning an integer
                the board is a grid of 2 by 3 integers
                data
                    the hidden is 1
                trap errors
                    return 0
                return 1
            To Hello, returning a string
                return "ok"
            To Stop, returning an integer
                exit the process with 2
                return 0
            Allow running scripts
            To Sourced, returning an integer
                source the script "missing.ps1"
                remove the module "Missing"
                redirect the errors of "x" to the output
                return 1
            To Folder, returning a string
                return the script folder
            To Chain, returning a boolean
                return 1 is equal to 0 or else 1 is equal to 1 and then 2 is equal to 2
            """);

        Assert.Contains("enum Color", script);
        Assert.Contains("static bool Keep", script);
        Assert.Contains("static string Hello", script);
        Assert.Contains("MachineHaltException", script);
        Assert.Contains("InvokeScript", script);
        Assert.Contains("Remove-Module", script);
        Assert.Contains("||", script);
        Assert.Contains("&&", script);
    }

    [Fact]
    public void A_paragraph_can_describe_a_loop_and_pronouns_point_at_it()
    {
        var total = Run("""
            To Total, returning an integer
                the total is 0
                for each number from 1 to 3, add it to the total
                return it
            """, "Total");
        Assert.Equal(6L, total);

        var skipped = Run("""
            To Total, returning an integer
                the total is 0
                for each number from 1 to 4, add it to the total. If it is 2, skip it.
                return the total
            """, "Total");
        Assert.Equal(10L, skipped);

        var otherwise = Run("""
            To Total, returning an integer
                the total is 0.
                for each number from 1 to 4, if it is 2, skip it, otherwise add it to the total.
                return the total
            """, "Total");
        Assert.Equal(8L, otherwise);

        var lengths = Run("""
            To Total, returning an integer
                the files are a list containing "a" and "bb"
                the total is 0
                for each file in the files, add its length to the total
                return the total
            """, "Total");
        Assert.Equal(3L, lengths);

        var them = Run("""
            To Tally, returning an integer
                the files are a list containing "a" and "bb"
                return the count of them
            """, "Tally");
        Assert.Equal(2L, them);

        var kept = Run("""
            To Tally, returning an integer
                the tally is 0
                keep increasing it until it is 3
                return it
            """, "Tally");
        Assert.Equal(3L, kept);

        var latest = Run("""
            To Latest, returning an integer
                the total is 0
                the tally is 1
                return it
            """, "Latest");
        Assert.Equal(1L, latest);
    }

    [Fact]
    public void A_keyword_can_be_a_name()
    {
        var summed = Run("""
            To Run, returning an integer
                return Count with 3
            To Count, given a which which is an integer, returning an integer
                the sum is 0
                the length is the which
                for each first from 1 to the length, add the first to the sum
                return the sum
            """, "Run");
        Assert.Equal(6L, summed);

        var measured = Run("""
            To Letters, returning an integer
                the string is "ab"
                the count is the length of the string
                return the count
            """, "Letters");
        Assert.Equal(2L, measured);

        var taken = Run("""
            To Kept, returning an integer
                the numbers are a list containing 4, 5 and 6
                the kept are the first 2 of the numbers
                return the count of the kept
            """, "Kept");
        Assert.Equal(2L, taken);
    }

    [Fact]
    public void A_pronoun_with_nothing_to_point_at_is_an_error()
    {
        var result = Compile("""
            To Broken, returning an integer
                return it
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E2032");
    }

    [Fact]
    public void Break_outside_a_loop_is_an_error()
    {
        var result = Compile("""
            To Bad, returning an integer
                break
                return 1
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E2031");
    }

    private static long Run(string source, string entry)
    {
        var value = Execute(source, entry);
        return value switch
        {
            long number => number,
            int number => number,
            decimal number when number == decimal.Truncate(number) => (long)number,
            _ => throw new Xunit.Sdk.XunitException($"Expected an integer, found {value} ({value?.GetType().Name}).")
        };
    }

    private static string RunText(string source, string entry)
    {
        var value = Execute(source, entry);
        return Assert.IsType<string>(value);
    }

    private static object? Execute(string source, string entry)
    {
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, entry, "{}");
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors) + Environment.NewLine + result.GeneratedCSharp);
        return exec.Value;
    }

    private static string Script(string source)
    {
        var result = Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.GeneratedCSharp!;
    }

    private static CompilationResult Compile(string source) =>
        new CompilationPipeline().Compile(source, Path.Combine(Path.GetTempPath(), "constructs.writ"));
}
