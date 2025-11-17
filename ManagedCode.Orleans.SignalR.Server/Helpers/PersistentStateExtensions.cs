using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Storage;

namespace ManagedCode.Orleans.SignalR.Server.Helpers;

internal static class PersistentStateExtensions
{
    public static async Task<bool> WriteStateSafeAsync<TState>(this IPersistentState<TState> state, Func<TState, bool> applyChanges)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(applyChanges);

        while (true)
        {
            try
            {
                if (!applyChanges(state.State))
                {
                    return false;
                }

                await state.WriteStateAsync();
                return true;
            }
            catch (InconsistentStateException)
            {
                await state.ReadStateAsync();
            }
        }
    }
}
