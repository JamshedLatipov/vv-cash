using System;
using System.Security.Cryptography;
using System.Text;

namespace VvCash.Services.Queue;

/// <summary>Детерминированный поток псевдослучайных байт для перемешивания пула
/// номеров: SHA256 в режиме счётчика поверх дня, индекса кассы и общего секрета.
/// Тот же вход даёт тот же поток — это то, что позволяет перезапуску в середине
/// дня воспроизвести тот же порядок, а не потерять его.
///
/// Раньше сид собирали как <c>BitConverter.ToInt32(SHA256Hash, 0)</c> — первые
/// 4 из 32 байт хэша, скормленные System.Random. Это оставляет всего 2^32
/// вариантов перестановки: клиент, записавший десяток талонов одного дня по
/// порядку, перебирает их на обычном железе за часы, восстанавливает сид,
/// а с ним — весь порядок выдачи и оборот между любыми двумя чеками. Здесь
/// System.Random не участвует вовсе; каждый следующий байт — это очередной
/// блок SHA256(материал || счётчик), так что предсказать поток можно только
/// подобрав сам материал (день, кассу, секрет), а не 32-битный сид поверх
/// него.</summary>
internal sealed class QueueShuffleKeystream
{
    private readonly byte[] _material;
    private long _blockCounter;
    private byte[] _block;
    private int _blockPos;

    public QueueShuffleKeystream(string day, int tillIndex, string secret)
    {
        _material = Encoding.UTF8.GetBytes($"{day}|{tillIndex}|{secret}");
        _block = HashBlock(0);
        _blockCounter = 1;
        _blockPos = 0;
    }

    private byte[] HashBlock(long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        var input = new byte[_material.Length + counterBytes.Length];
        Buffer.BlockCopy(_material, 0, input, 0, _material.Length);
        Buffer.BlockCopy(counterBytes, 0, input, _material.Length, counterBytes.Length);
        return SHA256.HashData(input);
    }

    private byte NextByte()
    {
        if (_blockPos == _block.Length)
        {
            _block = HashBlock(_blockCounter++);
            _blockPos = 0;
        }
        return _block[_blockPos++];
    }

    /// <summary>Равномерный индекс в [0, bound), методом отбраковки, а не остатком
    /// от деления. 256 значений байта почти никогда не делятся на bound поровну:
    /// например, для bound=180 значения 0..75 выпадают из двух байтовых значений
    /// каждое, а 76..179 — из одного, и «NextByte() % bound» тихо и навсегда даёт
    /// младшим индексам перевес. Для перемешивания это не косметика: тот же
    /// перекос делает ранние позиции чуть предсказуемее, а вся конструкция
    /// существует именно для того, чтобы позицию нельзя было предсказать. Здесь
    /// значения выше наибольшего кратного bound отбрасываются и байт берётся
    /// заново — это и убирает перекос, а не просто прячет его.
    ///
    /// Bound ограничен 256 (один байт) — этого хватает на любой вызов из этого
    /// пула (наибольший — длина среза, 180). Тому, кому понадобится больше,
    /// нужно тянуть несколько байт на отбраковку, а не убирать её.</summary>
    public int NextIndex(int bound)
    {
        if (bound <= 0 || bound > 256)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Must be in (0, 256].");
        if (bound == 1) return 0;

        var limit = 256 - (256 % bound);
        byte draw;
        do
        {
            draw = NextByte();
        } while (draw >= limit);

        return draw % bound;
    }
}
