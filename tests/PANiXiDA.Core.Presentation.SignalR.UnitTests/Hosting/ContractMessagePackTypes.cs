using PolyType;

namespace PANiXiDA.Core.Presentation.SignalR.UnitTests.Hosting;

[GenerateShapeFor<int>]
[GenerateShapeFor<bool>]
[GenerateShapeFor<string>]
[GenerateShapeFor<ContractRequest>]
[GenerateShapeFor<ContractResponse>]
[GenerateShapeFor<ScalarContract>]
[GenerateShapeFor<DateTimeOffset>]
internal partial class ContractMessagePackTypes;
