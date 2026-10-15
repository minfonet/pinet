# Stage 7e - Image input (Implementation Notes)

- Ergonomic factories: `ChatMessage.User(params ContentBlock[])` /
  `User(IReadOnlyList<ContentBlock>)` / `Assistant(...)` build multi-block
  messages without the record ctor.
- `InteractiveLoop.RunTurnAsync` expands `@path.{png,jpg,jpeg,gif,webp,bmp,svg}`
  tokens before sending (`ExpandImagesAsync`):
  - regex extraction (`@token` followed by whitespace/end), paths resolved
    against the session cwd (not `Environment.CurrentDirectory`);
  - file bytes base64-encoded, MIME inferred from the extension;
  - unresolvable/unreadable tokens stay in the text verbatim (yellow notice on
    I/O errors); prompt text is otherwise preserved byte-for-byte.
- Vision guard: `ModelDefinition.SupportsVision` false (or model unknown) →
  attachments skipped with a clear yellow `[image] … does not accept image input`
  notice; the text prompt is still sent.
- `TurnRunner.RunAsync(ChatMessage, ct)` overload carries the message with image
  blocks through the shared turn/persist path.
- Tests: `InteractiveLoopTests` image section (attach, guard, missing file).
- Deferred: clipboard/paste images (platform-dependent, optional per spec §7).
