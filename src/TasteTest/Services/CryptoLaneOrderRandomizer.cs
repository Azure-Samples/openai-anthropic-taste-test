using System.Security.Cryptography;

namespace TasteTest.Services;

public sealed class CryptoLaneOrderRandomizer : ILaneOrderRandomizer
{
    public bool PlaceOpenAIFirst() => RandomNumberGenerator.GetInt32(2) == 0;
}
