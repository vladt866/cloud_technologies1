using RRC.Abstractions;
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
}