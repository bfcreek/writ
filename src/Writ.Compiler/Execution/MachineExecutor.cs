using System.Text;
using Writ.Runtime;

namespace Writ.Compiler.Execution;

/// <summary>Compiles generated C# in-process and invokes a WritProgram entry on the Machine runtime.</summary>
public sealed class MachineExecutor
{
    public bool DryRun { get; init; }

    public ExecutionResult Execute(string generatedCSharp, string entryFunction, string? inputJson)
    {
        CapturingMachineIo io = new();
        try
        {
            Machine.Io = io;
            Machine.BindAmbient(DryRun);
            var value = Machine.InvokeGuest(generatedCSharp, entryFunction, inputJson);

            return ExecutionResult.Ok(Normalize(value)) with
            {
                Output = io.Output,
                CapabilityCalls = Machine.CapabilityCalls
            };
        }
        catch (MachineHaltException ex)
        {
            return ExecutionResult.Failed([ex.Message]) with { Output = io.Output, CapabilityCalls = Machine.CapabilityCalls };
        }
        catch (WritCapabilityException ex)
        {
            return ExecutionResult.Failed([$"{ex.Alias}.{ex.Operation} ({ex.Provider}): {ex.Message}"]) with
            {
                Output = io.Output,
                CapabilityCalls = Machine.CapabilityCalls
            };
        }
        catch (Exception ex)
        {
            return ExecutionResult.Failed([ex.Message]) with { Output = io.Output, CapabilityCalls = Machine.CapabilityCalls };
        }
        finally
        {
            Machine.Unbind();
        }
    }

    private static object? Normalize(object? value) => value switch
    {
        WritData data => data.ToDictionary(),
        System.Collections.IEnumerable enumerable and not string => enumerable.Cast<object?>().Select(Normalize).ToList(),
        _ => value
    };

    private sealed class CapturingMachineIo : IMachineIo
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public void Say(string text) => _output.AppendLine(text);

        public string ReadLine(string? prompt) => "";

        public void Clear()
        {
        }

        public void Wait(TimeSpan duration)
        {
        }
    }
}
