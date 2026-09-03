namespace TasteTest.Services;

public sealed record ModelIdentity(
    string Provider,
    string ModelId,
    string Protocol,
    string Sdk);
