namespace CodeSharp.Core;

public enum AgentExecutionMode
{
    Execute,
    Planning
}

public static class AgentExecutionModeExtensions
{
    public static string AsString(this AgentExecutionMode mode) => mode switch
    {
        AgentExecutionMode.Execute => "execute",
        AgentExecutionMode.Planning => "planning",
        _ => "execute"
    };
}
