using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Queue;

/// <summary>Досылает буфер очереди на таймере, пока касса жива. Отдельно от
/// SyncService, который ходит к бэкенду раз в несколько минут (см.
/// ISettingsService.SyncIntervalMinutes): сосед по локальной сети отвечает за
/// секунды, а не минуты, и держать заказ покупателя в буфере всё это время
/// незачем — 15 секунд и есть тот интервал (см. <see cref="Interval"/>).
///
/// Тот же приём, что PosViewModel.StartBackgroundSync: CancellationTokenSource
/// плюс Task.Run с Task.Delay(interval, token) внутри цикла. Но не сам
/// StartBackgroundSync и не часть PosViewModel — этот цикл заводится один раз
/// на весь процесс из App.axaml.cs, пока роль очереди не Off, и не зависит от
/// того, вошёл ли кто-то в кассу: буфер может ждать отправки и до входа
/// кассира, и после его выхода.
///
/// Task.Run кладёт цикл на поток из пула потоков — фоновый (IsBackground =
/// true) по умолчанию в .NET, — поэтому он не держит процесс живым сам по
/// себе: закрытие главного окна и без явной остановки этого цикла завершает
/// приложение тем же путём, каким оно уже завершается сегодня (см.
/// App.axaml.cs — ни QueueServer, ни этот цикл не останавливаются явно на
/// выходе, и оба безопасно обрываются вместе с процессом). Dispose() здесь всё
/// равно есть — не ради выхода из приложения, а для тестов и для повторного
/// Start(), которому нужно сначала погасить прежний цикл.</summary>
public class QueueFlushLoop : IDisposable
{
    /// <summary>15 секунд — решение спеки (Task 25), не подбор по месту:
    /// сосед на локальной сети отвечает за секунды, поэтому дольше ждать с
    /// повтором незачем, а чаще — только лишняя нагрузка на queue.db и на
    /// сеть без всякой пользы.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IQueueClient _client;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    /// <summary><paramref name="interval"/> — только для тестов; production
    /// код (App.axaml.cs) не передаёт его и получает <see cref="Interval"/>
    /// без изменений.</summary>
    public QueueFlushLoop(IQueueClient client, TimeSpan? interval = null)
    {
        _client = client;
        _interval = interval ?? Interval;
    }

    /// <summary>Заводит цикл. Безопасно звать повторно — прежний, если он
    /// был, сначала останавливается, тем же приёмом, что и
    /// PosViewModel.StartBackgroundSync.</summary>
    public void Start()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _client.FlushAsync();
                }
                catch (Exception ex)
                {
                    // Фоновая работа — сбой соседней кассы (недоступна,
                    // отвечает мусором, что угодно) не должен уронить эту.
                    // Тот же принцип, что и у остального оборудования в этом
                    // приложении: залогировать и продолжить, а не бросить
                    // наружу — здесь наружи для исключения и вовсе нет,
                    // Task.Run в фоне без await.
                    Console.WriteLine(
                        $"[QueueFlushLoop] FlushAsync failed: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    await Task.Delay(_interval, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
