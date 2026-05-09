using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolActionDescriptorProvider
{
    ToolActionDescriptor ResolveActionDescriptor(string argumentsJson);
}
