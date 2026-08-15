using AgentService.Infrastructure.AgentRuntime;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>驗證 Runtime 的跨工具硬性配額，不依賴實際模型或外部服務。</summary>
public sealed class AgentRuntimeRegressionTests
{
    [Fact]
    public async Task WrapTools_超過本輪上限時_回傳結構化BudgetExhausted()
    {
        var invocationCount = 0;
        var function = AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)(_ =>
            {
                invocationCount++;
                return Task.FromResult("ok");
            }),
            "regression_tool",
            "迴歸測試工具");

        var wrapped = AgentRuntime.WrapTools([function], maximumCalls: 2);
        var arguments = new AIFunctionArguments();

        Assert.Equal("ok", (await wrapped[0].InvokeAsync(arguments))?.ToString());
        Assert.Equal("ok", (await wrapped[0].InvokeAsync(arguments))?.ToString());
        var result = await wrapped[0].InvokeAsync(arguments);

        // 不同 MAF adapter 可能直接保留 Dictionary，或先以 JsonElement
        // 封送工具回傳；測試只驗證穩定的結構化欄位，不綁定 adapter 表示法。
        var status = result switch
        {
            IReadOnlyDictionary<string, object?> payload => payload["status"]?.ToString(),
            JsonElement json when json.ValueKind == JsonValueKind.Object =>
                json.GetProperty("status").GetString(),
            _ => throw new Xunit.Sdk.XunitException(
                $"工具預算回傳型別不符合契約：{result?.GetType().FullName ?? "null"}"),
        };
        Assert.Equal("budget_exhausted", status);
        Assert.Equal(2, invocationCount);
    }
}
