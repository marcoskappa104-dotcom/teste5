using System;
using Mirror;

namespace RPG.Quest
{
    /// <summary>
    /// Estado de uma quest ATIVA ou COMPLETADA na "ficha" do jogador.
    /// Struct para ser eficiente em SyncList; usa string para QuestId
    /// (não há jeito limpo de fazer SyncList&lt;CustomClass&gt; em Mirror sem
    /// custos adicionais, e strings são triviais).
    ///
    /// Progresso é serializado como string CSV de inteiros — Mirror não
    /// suporta arrays dentro de structs SyncList nativamente. Decodificado
    /// localmente em ProgressArray.
    /// </summary>
    [Serializable]
    public struct QuestProgress : IEquatable<QuestProgress>
    {
        public string QuestId;
        public QuestState State;

        /// <summary>Progresso por objetivo, serializado como "5,0,2".</summary>
        public string ProgressCsv;

        /// <summary>
        /// Tempo (UnixSeconds) em que entrou no estado atual.
        /// Útil para dailies (saber quando expira) e analytics.
        /// </summary>
        public long StateTimestamp;

        public bool Equals(QuestProgress other)
            => QuestId == other.QuestId
            && State == other.State
            && ProgressCsv == other.ProgressCsv
            && StateTimestamp == other.StateTimestamp;

        public override bool Equals(object obj)
            => obj is QuestProgress p && Equals(p);

        public override int GetHashCode()
            => unchecked((QuestId?.GetHashCode() ?? 0)
                ^ ((int)State * 397)
                ^ (ProgressCsv?.GetHashCode() ?? 0));

        public static QuestProgress NewActive(string questId, int objectiveCount)
        {
            return new QuestProgress
            {
                QuestId        = questId,
                State          = QuestState.Active,
                ProgressCsv    = SerializeProgress(new int[objectiveCount]),
                StateTimestamp = NowUnix()
            };
        }

        // ── Helpers de progress encoding ──────────────────────────────────

        public int[] GetProgressArray(int expectedCount)
        {
            return DeserializeProgress(ProgressCsv, expectedCount);
        }

        public QuestProgress WithProgress(int[] progress, QuestState newState = QuestState.Active)
        {
            var copy = this;
            copy.ProgressCsv    = SerializeProgress(progress);
            copy.State          = newState;
            copy.StateTimestamp = NowUnix();
            return copy;
        }

        public QuestProgress WithState(QuestState newState)
        {
            var copy = this;
            copy.State          = newState;
            copy.StateTimestamp = NowUnix();
            return copy;
        }

        public static string SerializeProgress(int[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            var sb = new System.Text.StringBuilder(arr.Length * 3);
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i]);
            }
            return sb.ToString();
        }

        public static int[] DeserializeProgress(string csv, int expectedCount)
        {
            var result = new int[expectedCount];
            if (string.IsNullOrEmpty(csv) || expectedCount == 0) return result;

            var parts = csv.Split(',');
            int limit = parts.Length < expectedCount ? parts.Length : expectedCount;
            for (int i = 0; i < limit; i++)
            {
                if (int.TryParse(parts[i], out int v) && v >= 0)
                    result[i] = v;
            }
            return result;
        }

        private static long NowUnix()
            => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// Estados possíveis de uma quest para o jogador.
    /// Quests não aceitas simplesmente não aparecem na lista de progresso.
    /// </summary>
    public enum QuestState : byte
    {
        Active        = 0, // Aceita, objetivos em progresso
        ReadyToTurnIn = 1, // Todos objetivos completos, aguardando entrega ao NPC
        Completed     = 2, // Entregue (final para não-repetíveis)
        Failed        = 3, // Falhou (timer, escolha, etc) — reservado para futuro
    }
}
