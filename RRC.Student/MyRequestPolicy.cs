/*using RRC.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public class MyRequestPolicy : IRequestPolicy
{
   
    private const int MaxAttempts = 5;               // Максимальное число попыток
    private const double BaseTimeoutMs = 100;        // Базовый тайм-аут в мс
    private const double TimeoutMultiplier = 2.0;    // Во сколько раз увеличивать тайм-аут
    private const double MaxTimeoutMs = 2000;        // Максимальный тайм-аут

    private const double BaseDelayMs = 50;           // Базовая задержка между попытками
    private const double DelayMultiplier = 1.5;      // Множитель задержки
    private const double MaxDelayMs = 1000;          // Максимальная задержка

    // Метод 1: Выбор тайм-аута для текущей попытки
    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        // Экспоненциальное увеличение тайм-аута с каждой попыткой
        double timeoutMs = BaseTimeoutMs * Math.Pow(TimeoutMultiplier, context.Attempt - 1);

        // Добавляем "дрожание" (jitter), чтобы избежать синхронизации запросов
        timeoutMs += random.Next(0, 50);

        // Ограничиваем максимальное значение
        timeoutMs = Math.Min(timeoutMs, MaxTimeoutMs);

        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    // Метод 2: Решение о повторной попытке после получения результата
    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        // Если запрос успешен — повторять не нужно
        if (observation.Outcome == RequestOutcome.Success)
        {
            return RetryDecision.NoRetry();
        }

        // Если исчерпали лимит попыток — сдаемся
        if (observation.Attempt >= MaxAttempts)
        {
            return RetryDecision.NoRetry();
        }

        // Если был тайм-аут, вычисляем задержку перед следующей попыткой
        double delayMs = BaseDelayMs * Math.Pow(DelayMultiplier, observation.Attempt - 1);

        // Добавляем jitter
        delayMs += random.Next(0, 50);

        // Ограничиваем максимальную задержку
        delayMs = Math.Min(delayMs, MaxDelayMs);

        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(delayMs));
    }
}*/

using RRC.Abstractions;
using System;
using System.Collections.Generic;

public class MyRequestPolicy : IRequestPolicy
{
    // === Параметры TCP (RFC 6298) ===
    private const double Alpha = 0.125;   // 1/8 — сглаживание SRTT
    private const double Beta = 0.25;    // 1/4 — сглаживание RTTVAR
    private const double K = 4.0;     // RTO = SRTT + K * RTTVAR

    private const double MinRtoMs = 200.0;   // Минимальный RTO
    private const double MaxRtoMs = 3000.0;  // Максимальный RTO
    private const int MaxAttempts = 6;       // Как в TCP — до 6 попыток

    // === Состояние TCP-оценщика (одно на все запросы) ===
    private double _srtt = -1;   // -1 означает "ещё не инициализировано"
    private double _rttvar = 0;

    // === Счётчик повторов для каждого запроса ===
    private readonly Dictionary<long, int> _retransmitCount = new();

    // 
    // Метод 1: Выбор тайм-аута (аналог RTO в TCP)
    // 
    public TimeSpan GetTimeout(RequestContext context, Random random)
    {
        // Базовый RTO: если измерений ещё нет — стартовое значение
        double rto = _srtt < 0
            ? 300.0                                  // TCP использует 1 секунду, но у нас задержки меньше
            : _srtt + K * _rttvar;                   // Основная формула RFC 6298

        // Exponential backoff: удваиваем RTO при каждой повторной передаче
        int retx = _retransmitCount.TryGetValue(context.RequestId, out var c) ? c : 0;
        rto *= Math.Pow(2, retx);

        // Ограничиваем сверху и снизу
        rto = Math.Max(rto, MinRtoMs);
        rto = Math.Min(rto, MaxRtoMs);

        // Небольшой jitter, чтобы клиенты не синхронизировались
        rto += random.Next(0, 20);

        return TimeSpan.FromMilliseconds(rto);
    }

    // ------------------------------------------------------------
    // Метод 2: Решение о повторе (аналог обработки ACK / тайм-аута)
    // ------------------------------------------------------------
    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        if (observation.Outcome == RequestOutcome.Success)
        {
            // Karn's algorithm: обновляем RTT только по первой попытке.
            // Если запрос повторялся, мы не знаем, ответ на какую попытку пришёл.
            if (observation.Attempt == 1)
            {
                UpdateRttEstimates(observation.Elapsed.TotalMilliseconds);
            }

            // Успех — чистим состояние запроса
            _retransmitCount.Remove(observation.RequestId);
            return RetryDecision.NoRetry();
        }

        // Тайм-аут. Проверяем, не исчерпали ли лимит попыток.
        if (observation.Attempt >= MaxAttempts)
        {
            _retransmitCount.Remove(observation.RequestId);
            return RetryDecision.NoRetry();
        }

        // Увеличиваем счётчик повторов для этого запроса.
        // В следующий раз GetTimeout вернёт удвоенный RTO.
        int current = _retransmitCount.TryGetValue(observation.RequestId, out var c) ? c : 0;
        _retransmitCount[observation.RequestId] = current + 1;

        // В TCP нет паузы между retransmission — но здесь она полезна,
        // чтобы сервер успел разгрузиться. Небольшая, с jitter.
        double delayMs = 10 + random.Next(0, 30);
        return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(delayMs));
    }

    // 
    // Обновление SRTT и RTTVAR по формуле RFC 6298
    // 
    private void UpdateRttEstimates(double r)
    {
        if (_srtt < 0)
        {
            // Первое измерение: SRTT = R, RTTVAR = R / 2
            _srtt = r;
            _rttvar = r / 2.0;
        }
        else
        {
            // RTTVAR = (1 - β) * RTTVAR + β * |SRTT - R|
            _rttvar = (1 - Beta) * _rttvar + Beta * Math.Abs(_srtt - r);
            // SRTT = (1 - α) * SRTT + α * R
            _srtt = (1 - Alpha) * _srtt + Alpha * r;
        }
    }
}