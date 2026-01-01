namespace Orchestrator.Exceptions
{
    public class NodeNotFoundException : Exception
    {
        public NodeNotFoundException(string nodeId)
            : base($"Node with ID '{nodeId}' was not found.")
        {
        }
    }
}
