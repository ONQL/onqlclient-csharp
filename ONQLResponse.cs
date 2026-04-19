namespace ONQL
{
    /// <summary>
    /// Represents a parsed response from the ONQL server.
    /// </summary>
    public class ONQLResponse
    {
        /// <summary>
        /// The request ID that this response corresponds to.
        /// </summary>
        public string RequestId { get; }

        /// <summary>
        /// The source identifier returned by the server.
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// The response payload (typically JSON).
        /// </summary>
        public string Payload { get; }

        public ONQLResponse(string requestId, string source, string payload)
        {
            RequestId = requestId;
            Source = source;
            Payload = payload;
        }

        public override string ToString()
        {
            return $"ONQLResponse(RequestId={RequestId}, Source={Source}, Payload={Payload})";
        }
    }
}
