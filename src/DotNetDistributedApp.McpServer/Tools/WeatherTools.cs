using DotNetDistributedApp.McpServer.Clients;
using ModelContextProtocol.Server;

namespace DotNetDistributedApp.McpServer.Tools;

[McpServerToolType]
public class WeatherTools(WeatherApiClient weatherApiClient) { }
