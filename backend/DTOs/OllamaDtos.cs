namespace RabbitIC.Api.DTOs
{

        public class LoginRequest {
            public string Username { get; set; } = string.Empty;
            public string Secret { get; set; } = string.Empty;
        };



        public class ChatRequest
        {
            public int? ConversationId {  get; set; }
            public string Model { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;

            /// <summary>Per-message opt-in: ground this one turn on live web results.</summary>
            public bool WebSearch { get; set; }
        };


        /// <summary>Proxied web search. MaxResults is clamped to 1..10 server-side.</summary>
        public class SearchRequest
        {
            public string Query { get; set; } = string.Empty;
            public int? MaxResults { get; set; }
        };

        /// <summary>Proxied single-page fetch. Url must be absolute http(s).</summary>
        public class FetchPageRequest
        {
            public string Url { get; set; } = string.Empty;
        };



        /// <summary>Workspace chat: stateless, RAG-grounded, never saved to Conversations.</summary>
        public class WorkspaceChatRequest
        {
            public int? DocumentId { get; set; }   // pin to one document; null = search all my chunks
            public string Model { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        };

        public class ExtractRequest
        {
            /// <summary>Chat model for the LLM fallback path. Optional — config default applies.</summary>
            public string? Model { get; set; }
        };

        /// <summary>Human sign-off on an extraction run.</summary>
        public class ConfirmExtractionRequest
        {
            /// <summary>The reviewed (possibly edited) extraction payload.</summary>
            public System.Text.Json.JsonElement ResultJson { get; set; }

            /// <summary>Which field paths changed from machine output, plus any override reason.</summary>
            public System.Text.Json.JsonElement Edits { get; set; }
        };

        public class ChatMsg
        {
            public string Role  { get; set; } = string.Empty;
            public string Content {get; set; } = string.Empty; 
        };
    
}
