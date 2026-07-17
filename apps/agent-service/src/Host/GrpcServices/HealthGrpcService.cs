using AgentService.Host.GrpcServices;
using Grpc.Core;

namespace AgentService.Host.GrpcServices;

public class HealthGrpcService : HealthService.HealthServiceBase
{
    private readonly ILogger<HealthGrpcService> _logger;

    public HealthGrpcService(ILogger<HealthGrpcService> logger)
    {
        _logger = logger;
    }

    public override Task<HealthCheckResponse> Check(
        HealthCheckRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("Health check requested for service: {Service}", request.Service);

        return Task.FromResult(new HealthCheckResponse
        {
            Status = HealthCheckResponse.Types.ServingStatus.Serving
        });
    }
}
