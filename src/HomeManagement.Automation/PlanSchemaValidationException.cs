namespace HomeManagement.Automation;

/// <summary>
/// Raised when planner output violates the strict plan schema contract.
/// This is treated as a hard reject by the API and must not be persisted.
/// </summary>
internal sealed class PlanSchemaValidationException : Exception
{
    public PlanSchemaValidationException(string message) : base(message)
    {
    }
}
