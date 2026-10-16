import { useQueryClient } from '@tanstack/react-query';
import React from 'react';
import { getDeviceGetSavesQueryKey, getDeviceGetStatusQueryKey, useDeviceGetStatus } from '../sdk/device/device.gen';
import { DeviceBankMode, RuntimeSystem } from '../sdk/model';
import { useSettingsGet } from '../sdk/settings/settings.gen';

const STATUS_REFRESH_MS = 10_000;

/**
 * The remote bank only exists in the Android app.
 */
export const useRemoteBankAvailable = (): boolean => useSettingsGet().data?.data.runtimeSystem === RuntimeSystem.ANDROID;

/**
 * Mode, connection and transfers of the remote bank.
 * It is checked regularly: the connection and the transfers change without any action of the user.
 */
export const useRemoteStatus = () => {
    const available = useRemoteBankAvailable();

    const query = useDeviceGetStatus({
        query: {
            enabled: available,
            refetchInterval: STATUS_REFRESH_MS,
            refetchOnWindowFocus: true,
            staleTime: 0,
        },
    });

    const status = available ? query.data?.data : undefined;

    return {
        available,
        status,
        isRemote: status?.mode === DeviceBankMode.REMOTE,
        query,
    };
};

/**
 * Reloads what depends on the transfers, without waiting for the next regular check.
 */
export const useRefreshRemoteStatus = () => {
    const queryClient = useQueryClient();

    return React.useCallback(async () => {
        await Promise.all([
            queryClient.invalidateQueries({ queryKey: getDeviceGetStatusQueryKey() }),
            queryClient.invalidateQueries({ queryKey: getDeviceGetSavesQueryKey() }),
        ]);
    }, [ queryClient ]);
};

/**
 * Everything displayed depends on the bank in use (local or the server one):
 * after a change of mode, or of what is on the server, it is all loaded again.
 */
export const useReloadAll = () => {
    const queryClient = useQueryClient();

    return React.useCallback(() => queryClient.invalidateQueries(), [ queryClient ]);
};
