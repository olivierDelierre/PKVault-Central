import { Stack, Text } from '@mantine/core';
import { TriangleAlertIcon } from 'lucide-react';
import React from 'react';
import { ConflictReason, getConflictExplanation, getErrorMessage } from '../data/remote/remote-transfers';
import { useReloadAll, useRefreshRemoteStatus } from '../data/remote/use-remote-status';
import { useDeviceResolveConflict } from '../data/sdk/device/device.gen';
import { type DeviceTransferDTO, TransferConflictResolution } from '../data/sdk/model';
import { UIButton } from '../ui/form/button/ui-button';
import { UIConfirmPopover } from '../ui/popover/ui-confirm-popover';

type RemoteConflictActionsProps = {
    transfer: DeviceTransferDTO;
    onError: (message: string | undefined) => void;
};

/**
 * The choices offered when a save cannot be delivered by itself.
 * Nothing is written on the device before one of them is chosen.
 */
export const RemoteConflictActions: React.FC<RemoteConflictActionsProps> = ({ transfer, onError }) => {
    const resolveMutation = useDeviceResolveConflict();
    const refreshStatus = useRefreshRemoteStatus();
    const reloadAll = useReloadAll();

    const resolve = async (resolution: TransferConflictResolution) => {
        onError(undefined);
        try {
            await resolveMutation.mutateAsync({ localId: transfer.localId, params: { resolution } });
        } catch (e) {
            // ex: the server cannot cancel anymore because other changes were saved since
            onError(getErrorMessage(e));
        }
        // the bank of the server can have changed
        await Promise.all([ refreshStatus(), reloadAll() ]);
    };

    const reason = transfer.conflictReason;

    const cancelOnServer = <UIConfirmPopover
        label='Cancel the changes made on the server'
        description='The bank of the server goes back to how it was before these changes. The save of this device is not touched. Not possible if something else was saved on the server since.'
        icon={<TriangleAlertIcon />}
        color='blue'
        action={() => resolve(TransferConflictResolution.ABORT_ON_SERVER)}
    >
        <UIButton
            name={`remote-cancel-${transfer.localId}`}
            controlLabel='Cancel the changes made on the server'
            variant='filled'
            color='blue'
            size='compact-sm'
            fullWidth
        >
            Cancel the changes made on the server
        </UIButton>
    </UIConfirmPopover>;

    return <Stack gap='xs'>
        <Text size='sm'>{getConflictExplanation(reason)}</Text>

        {reason === ConflictReason.DELIVERY_GONE
            ? <UIConfirmPopover
                label='Forget these changes'
                description='Nothing is written on this device.'
                icon={<TriangleAlertIcon />}
                color='red'
                action={() => resolve(TransferConflictResolution.DISCARD)}
            >
                <UIButton
                    name={`remote-discard-${transfer.localId}`}
                    controlLabel='Forget these changes'
                    variant='filled'
                    color='red'
                    size='compact-sm'
                    fullWidth
                >
                    Forget these changes
                </UIButton>
            </UIConfirmPopover>
            : <>
                {cancelOnServer}

                <UIConfirmPopover
                    label={reason === ConflictReason.DEVICE_SAVE_CHANGED
                        ? 'Replace the save of this device'
                        : 'Write the modified save'}
                    description={reason === ConflictReason.DEVICE_SAVE_CHANGED
                        ? 'What you played since is replaced by the modified save. A copy of the current save is kept in the app files.'
                        : 'The modified save is written on this device.'}
                    icon={<TriangleAlertIcon />}
                    color='red'
                    action={() => resolve(TransferConflictResolution.OVERWRITE_DEVICE)}
                >
                    <UIButton
                        name={`remote-overwrite-${transfer.localId}`}
                        controlLabel='Write the modified save'
                        variant='default'
                        size='compact-sm'
                        fullWidth
                    >
                        {reason === ConflictReason.DEVICE_SAVE_CHANGED
                            ? 'Replace the save of this device'
                            : reason === ConflictReason.WRITE_FAILED ? 'Try again' : 'Write the modified save'}
                    </UIButton>
                </UIConfirmPopover>
            </>}
    </Stack>;
};
