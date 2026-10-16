import { Alert, Badge, Card, Group, Stack, Text } from '@mantine/core';
import { CloudUploadIcon, PencilIcon, SmartphoneIcon, TriangleAlertIcon } from 'lucide-react';
import React from 'react';
import { ConflictReason, getErrorMessage, getTransferLabel, needsAttention, TransferState } from '../../data/remote/remote-transfers';
import { useReloadAll, useRefreshRemoteStatus, useRemoteStatus } from '../../data/remote/use-remote-status';
import { useDeviceGetSaves, useDeviceOpenOnServer } from '../../data/sdk/device/device.gen';
import { EntityContext, type DeviceSaveDTO } from '../../data/sdk/model';
import { getEntityContextGenerationName } from '../../data/util/get-entity-context-generation-name';
import { useStaticData } from '../../hooks/use-static-data';
import { getGameInfos } from '../../pokedex/details/util/get-game-infos';
import { RemoteStatusModal } from '../../remote/remote-status-modal';
import { UIButton } from '../../ui/form/button/ui-button';
import { UIGameExpanded } from '../../ui/storage/storage-panel/game-list/ui-game-expanded';

type DeviceSavesSectionProps = {
    /** To open a save of the server in the storage page. */
    onEdit: (saveId: number) => void;
};

/**
 * In remote mode: the saves of this device, which can be sent to the server to be edited there.
 * (The list of saves below is the one of the server.)
 */
export const DeviceSavesSection: React.FC<DeviceSavesSectionProps> = ({ onEdit }) => {
    const { isRemote } = useRemoteStatus();

    const savesQuery = useDeviceGetSaves({
        query: {
            enabled: isRemote,
            refetchInterval: 10_000,
            staleTime: 0,
        },
    });

    const [ error, setError ] = React.useState<string>();
    const [ detailsOpened, setDetailsOpened ] = React.useState(false);

    if (!isRemote) {
        return null;
    }

    const saves = [ ...(savesQuery.data?.data ?? []) ]
        .sort((a, b) => (a.save.lastWriteTime > b.save.lastWriteTime ? -1 : 1));

    return <Card>
        <Card.Section inheritPadding withBorder>
            <Group py='sm' gap='sm'>
                <SmartphoneIcon />
                <Text fw='bold'>Saves of this device</Text>
            </Group>
        </Card.Section>

        <Card.Section inheritPadding py='sm'>
            <Stack gap='sm'>
                <Text size='sm' c='dimmed'>
                    Send a save to the server to edit it with the bank of the server.
                    Once saved, the modified save is written back here.
                </Text>

                {error && <Alert variant='outline' color='red' icon={<TriangleAlertIcon />}>{error}</Alert>}

                {saves.length === 0 && <Text c='dimmed'>No save on this device.</Text>}

                <Group>
                    {saves.map(deviceSave => <DeviceSaveItem
                        key={deviceSave.save.id}
                        deviceSave={deviceSave}
                        onEdit={onEdit}
                        onError={setError}
                        onDetails={() => setDetailsOpened(true)}
                    />)}
                </Group>
            </Stack>
        </Card.Section>

        <RemoteStatusModal opened={detailsOpened} onClose={() => setDetailsOpened(false)} />
    </Card>;
};

const DeviceSaveItem: React.FC<{
    deviceSave: DeviceSaveDTO;
    onEdit: (saveId: number) => void;
    onError: (message: string | undefined) => void;
    onDetails: () => void;
}> = ({ deviceSave: { save, transfer }, onEdit, onError, onDetails }) => {
    const staticData = useStaticData();

    const openMutation = useDeviceOpenOnServer();
    const refreshStatus = useRefreshRemoteStatus();
    const reloadAll = useReloadAll();

    const versionObj = staticData.versions[ save.displayedVersion ];

    const isOpen = transfer?.state === TransferState.UPLOADED || transfer?.state === TransferState.COMMIT_REQUESTED;
    const serverSaveId = transfer?.saveId ?? save.id;

    return <UIGameExpanded
        id={save.id.toString()}
        generation={getEntityContextGenerationName(save.context ?? EntityContext.None, true)}
        label={versionObj?.name}
        imgSrc={getGameInfos(save.displayedVersion).img}
        ot={save.trainerName}
        otGender={save.trainerGender}
        tid={save.tid}
        caughtCount={save.dexCaughtCount}
        ownedCount={save.ownedCount}
        shinyCount={save.shinyCount}
        playTime={save.playTime}
        language={staticData.languages[ save.language ]}
        path={save.path}
        onSelect={isOpen ? () => onEdit(serverSaveId) : undefined}
        actions={<Stack gap='xs' w='100%'>
            {transfer && <Group gap='xs' wrap='nowrap' justify='space-between'>
                <Badge variant='light' color={needsAttention(transfer) ? 'orange' : 'blue'}>
                    {getTransferLabel(transfer)}
                </Badge>
                {(needsAttention(transfer) || transfer.lastError) &&
                    <UIButton
                        name={`device-save-details-${save.id}`}
                        controlLabel='Details'
                        onClick={onDetails}
                        variant='default'
                        size='compact-xs'
                    >
                        {transfer.conflictReason === ConflictReason.DEVICE_SAVE_CHANGED ? 'Decide' : 'Details'}
                    </UIButton>}
            </Group>}

            {!transfer && <UIButton
                name={`device-save-open-${save.id}`}
                controlLabel='Send to the server'
                leftSection={<CloudUploadIcon />}
                variant='default'
                size='compact-xs'
                fullWidth
                onClick={async () => {
                    onError(undefined);
                    try {
                        await openMutation.mutateAsync({ saveId: save.id });
                    } catch (e) {
                        onError(getErrorMessage(e));
                    }
                    // the server has a new save to show
                    await Promise.all([ refreshStatus(), reloadAll() ]);
                }}
            >
                Send to the server
            </UIButton>}

            {isOpen && <UIButton
                name={`device-save-edit-${save.id}`}
                controlLabel='Edit on the server'
                leftSection={<PencilIcon />}
                variant='filled'
                size='compact-xs'
                fullWidth
                onClick={() => onEdit(serverSaveId)}
            >
                Edit on the server
            </UIButton>}
        </Stack>}
    />;
};
