using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeployManager.Hubs;

// Authenticated hub — only connected browser sessions receive push events.
// The server never receives messages from clients; it is the sole sender.
[Authorize]
public class DeployHub : Hub { }
