// Oddity — the API examples, written once.
//
// The Docs board, the public /oddity/docs page and the eval all read them from
// here, and the eval runs each one unchanged against a fake Ollama (DOC.6–DOC.9),
// so an example on the page is an example that has been run.
//
// Two placeholders and no others: {{BASE}} is the gateway base URL, {{KEY}} a
// gateway key. Plain ES module with no imports, so Node can load it as well.

export const EXAMPLES = [
  {
    id: 'curl',
    label: 'curl',
    language: 'bash',
    code: String.raw`# curl: the same call as Ollama, plus the key.
curl {{BASE}}/api/chat \
  -H "Authorization: Bearer {{KEY}}" \
  -d '{"model":"qwen3.8:27b","messages":[{"role":"user","content":"Add a paged GET endpoint for buyers."}]}'`,
  },
  {
    id: 'js',
    label: 'JavaScript',
    language: 'javascript',
    code: String.raw`// JavaScript: ollama 0.6 or later
import { Ollama } from 'ollama'

const oddity = new Ollama({ host: '{{BASE}}', headers: { Authorization: 'Bearer {{KEY}}' } })
const res = await oddity.chat({
  model: 'qwen3.8:27b',
  stream: false,
  messages: [{ role: 'user', content: 'What is the ship window?' }],
})
console.log(res.message.content)`,
  },
  {
    id: 'python',
    label: 'Python',
    language: 'python',
    code: String.raw`# Python: ollama 0.6 or later
from ollama import Client

oddity = Client(host='{{BASE}}', headers={'Authorization': 'Bearer {{KEY}}'})
for part in oddity.chat(model='qwen3.8:27b', messages=[{'role': 'user', 'content': 'Add a paged GET endpoint for buyers.'}], stream=True):
    print(part.message.content, end='')
print()`,
  },
  {
    id: 'csharp',
    label: 'C#',
    language: 'csharp',
    code: String.raw`// C#: .NET 8 or later, in a project. (A single-file "dotnet run app.cs" turns off
// the reflection-based JSON this anonymous object needs.)
using System.Net.Http.Json;

using var http = new HttpClient { BaseAddress = new Uri("{{BASE}}/"), Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.Authorization = new("Bearer", "{{KEY}}");

var res = await http.PostAsJsonAsync("api/chat", new
{
    model = "qwen3.8:27b",
    stream = false,
    messages = new[] { new { role = "user", content = "Monthly shipment report by buyer." } },
});
Console.WriteLine(await res.Content.ReadAsStringAsync());`,
  },
]

/** An example with its two placeholders filled in. */
export function fillExample(code, { base, key }) {
  return code.replaceAll('{{BASE}}', base).replaceAll('{{KEY}}', key)
}
