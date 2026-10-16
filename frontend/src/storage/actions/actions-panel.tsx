import React from 'react';
import { useRefreshRemoteStatus, useRemoteStatus } from '../../data/remote/use-remote-status';
import { useSettingsGet } from '../../data/sdk/settings/settings.gen';
import { useStorageDeleteActions, useStorageGetActions, useStorageSave } from '../../data/sdk/storage/storage.gen';
import { withErrorCatcher } from '../../error/with-error-catcher';
import { UIActionsPanel } from '../../ui/actions-panel/ui-actions-panel';
import { ActionLabel } from './action-label';
import { useActionDescription } from './hooks/use-action-description';

/**
 * Display current session actions, and save button.
 */
export const ActionsPanel: React.FC = withErrorCatcher('default', () => {
    const settingsQuery = useSettingsGet();
    const actionsQuery = useStorageGetActions();
    const deleteActionsMutation = useStorageDeleteActions();
    const saveMutation = useStorageSave();

    const getActionDescription = useActionDescription();

    // with the bank of the server, saving needs the server (and moves the modified save onto the device)
    const { status: remoteStatus, isRemote } = useRemoteStatus();
    const refreshRemoteStatus = useRefreshRemoteStatus();
    const serverUnavailable = isRemote && !!remoteStatus && (!remoteStatus.reachable || !remoteStatus.authorized);

    const actions = actionsQuery.data?.data ?? [];

    return <UIActionsPanel
        data={actions.map((action, index) => {
            return {
                type: action.type,
                label: <ActionLabel {...action} />,
                description: getActionDescription(action),
                index,
            };
        })}
        onDelete={(index: number) => deleteActionsMutation.mutateAsync({
            params: {
                actionIndexToRemoveFrom: index,
            },
        })}
        onSave={settingsQuery.data?.data.demoMode || serverUnavailable
            ? undefined
            : async () => {
                try {
                    await saveMutation.mutateAsync();
                } finally {
                    // the delivery to the device is part of the answer
                    if (isRemote) {
                        await refreshRemoteStatus();
                    }
                }
            }}
    />;
});
