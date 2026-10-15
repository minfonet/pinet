# Stage 7e - Image input (Implementation Notes)
- Added ergonomic ChatMessage factories (User/Assistant accept ContentBlock params/lists)
- Added @image expansion helper in InteractiveLoop (resolve relative to cwd, base64-encode, infer MIME)
- Vision guard checks model.SupportsVision before sending images
