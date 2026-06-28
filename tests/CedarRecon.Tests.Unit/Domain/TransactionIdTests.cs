using CedarRecon.Domain.ValueObjects;
using Shouldly;

namespace CedarRecon.Tests.Unit.Domain;

public sealed class TransactionIdTests
{
    [Fact]
    public void New_CreatesNonEmptyGuid()
    {
        var id = TransactionId.New();
        id.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void From_EmptyGuid_Throws()
    {
        Should.Throw<ArgumentException>(() => TransactionId.From(Guid.Empty));
    }

    [Fact]
    public void From_ValidGuid_Roundtrips()
    {
        var guid = Guid.NewGuid();
        var id = TransactionId.From(guid);
        id.Value.ShouldBe(guid);
    }

    [Fact]
    public void From_InvalidString_Throws()
    {
        Should.Throw<ArgumentException>(() => TransactionId.From("not-a-guid"));
    }

    [Theory]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    public void From_ValidGuidString_Parses(string guidStr)
    {
        var id = TransactionId.From(guidStr);
        id.Value.ToString().ShouldBe(guidStr);
    }

    [Fact]
    public void ImplicitConversion_ToGuid_Works()
    {
        var id = TransactionId.New();
        Guid guid = id;
        guid.ShouldBe(id.Value);
    }

    [Fact]
    public void TwoDistinctIds_AreNotEqual()
    {
        var id1 = TransactionId.New();
        var id2 = TransactionId.New();
        id1.ShouldNotBe(id2);
    }
}
