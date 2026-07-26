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
        };



        public class ChatMsg
        {
            public string Role  { get; set; } = string.Empty;
            public string Content {get; set; } = string.Empty; 
        };
    
}
