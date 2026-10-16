import { Alert, Badge, Card, Group, Loader, Modal, Stack, Text } from '@mantine/core';
import { CloudOffIcon, RefreshCwIcon, TriangleAlertIcon } from 'lucide-react';
import React from 'react';
import { getErrorMessage, getTransferLabel, isInProgress, TransferState } from '../data/remote/remote-transfers';
import { useRefreshRemoteStatus, useRemoteStatus } from '../data/remote/use-remote-status';
import { useDeviceCloseTransfer, useDeviceSync } from '../data/sdk/device/device.gen';
import type { DeviceTransferDTO } from '../data/sdk/model';
import { UIButton } from '../ui/form/button/ui-button';
import { FocusScope } from '../ui/interaction/focus/scope/focus-scope';
import { RemoteConflictActions } from './remote-conflict-actions';

type RemoteStatusModalProps = {
    opened: boolean;
    onClose: () => void;
};

/**
 * Connection with the server, and what happens to the saves exchanged with it:
 * where the user follows them and settles what cannot be settled automatically.
 */
export const RemoteStatusModal: React.FC<RemoteStatusModalProps> = ({ opened, onClose }) => {
    return <Modal
        opened={opened}
        keepMounted={false}
        onClose={onClose}
        size='md'
        title={<Text size='xl'>Remote bank</Text>}
    >
        <FocusScope id='remote-status-dialog' focusOnMount>
            <RemoteStatusContent />
        </FocusScope>
    </Modal>;
};

const RemoteStatusContent: React.FC = () => {
    const { status, isRemote } = useRemoteStatus();
    const refresh = useRefreshRemoteStatus();
    const syncMutation = useDeviceSync();

    const [ error, setError ] = React.useState<string>();

    if (!status) {
        return <Loader size='sm' />;
    }

    return <Stack>
        <Group justify='space-between' wrap='nowrap'>
            <Stack gap={0}>
                <Text fw='bold'>{isRemote ? 'Using the bank of the server' : 'Using the bank of this device'}</Text>
                {status.serverUrl && <Text size='sm' c='dimmed'>{status.serverUrl}</Text>}
            </Stack>

            {isRemote && (status.reachable && status.authorized
                ? <Badge color='green' variant='light'>Online</Badge>
                : <Badge color='red' variant='light' leftSection={<CloudOffIcon size={12} />}>
                    {status.authorized ? 'Offline' : 'Token refused'}
                </Badge>)}
        </Group>

        {isRemote && status.connectionError && !status.reachable &&
            <Alert variant='outline' color='red' icon={<CloudOffIcon />}>
                {status.connectionError}
                <br />Your saves stay as they are. Transfers go on by themselves once the server is back.
            </Alert>}

        {isRemote && !status.authorized &&
            <Alert variant='outline' color='red' icon={<TriangleAlertIcon />}>
                The server refused the access token. Check it in the settings.
            </Alert>}

        {error && <Alert variant='outline' color='red' icon={<TriangleAlertIcon />}>{error}</Alert>}

        {status.transfers.length === 0
            ? <Text c='dimmed'>No save is being exchanged with the server.</Text>
            : <Stack gap='sm'>
                {status.transfers.map(transfer => <TransferCard
                    key={transfer.localId}
                    transfer={transfer}
                    onError={setError}
                />)}
            </Stack>}

        <UIButton
            name='remote-status-sync'
            controlLabel='Check now'
            onClick={async () => {
                setError(undefined);
                try {
                    await syncMutation.mutateAsync();
                } catch (e) {
                    setError(getErrorMessage(e));
                }
                await refresh();
            }}
            leftSection={<RefreshCwIcon />}
            variant='default'
            fullWidth
        >
            Check now
        </UIButton>
    </Stack>;
};

const TransferCard: React.FC<{
    transfer: DeviceTransferDTO;
    onError: (message: string | undefined) => void;
}> = ({ transfer, onError }) => {
    const refresh = useRefreshRemoteStatus();
    const closeMutation = useDeviceCloseTransfer();

    const canClose = transfer.state === TransferState.UPLOADED || transfer.state === TransferState.COMMIT_REQUESTED;

    return <Card withBorder p='sm'>
        <Stack gap='xs'>
            <Group justify='space-between' wrap='nowrap'>
                <Text fw='bold' truncate>{transfer.fileName}</Text>
                <Group gap='xs' wrap='nowrap'>
                    {isInProgress(transfer) && !transfer.lastError && <Loader size='xs' />}
                    <Text size='sm'>{getTransferLabel(transfer)}</Text>
                </Group>
            </Group>

            {transfer.lastError &&
                <Text size='sm' c='red'>
                    Paused: {transfer.lastError}. It goes on by itself when possible.
                </Text>}

            {transfer.warning && <Text size='sm' c='orange'>{transfer.warning}</Text>}

            {transfer.state === TransferState.CONFLICT && <RemoteConflictActions transfer={transfer} onError={onError} />}

            {canClose && <UIButton
                name={`remote-close-${transfer.localId}`}
                controlLabel='Stop working on this save on the server'
                onClick={async () => {
                    onError(undefined);
                    try {
                        await closeMutation.mutateAsync({ localId: transfer.localId });
                    } catch (e) {
                        onError(getErrorMessage(e));
                    }
                    await refresh();
                }}
                variant='default'
                size='compact-sm'
                fullWidth
            >
                Stop working on this save on the server
            </UIButton>}
        </Stack>
    </Card>;
};
