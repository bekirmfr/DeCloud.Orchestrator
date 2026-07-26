import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

// Renders untrusted third-party Markdown SAFELY: react-markdown builds a React
// element tree (never innerHTML), and raw HTML is NOT enabled (no rehype-raw),
// so any embedded <script> / <img onerror> / event handlers are treated as inert
// text, not executed — fail-closed against a malicious template author. GFM adds
// tables / strikethrough / task-lists. Styling lives in the `.md` class
// (global.css), so there's no per-element component map to maintain.
export function Markdown({ children }: { children: string }) {
  return (
    <div className="md">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{children}</ReactMarkdown>
    </div>
  );
}
