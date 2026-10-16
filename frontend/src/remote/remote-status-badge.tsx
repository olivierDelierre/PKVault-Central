import { Badge, Tooltip } from '@mantine/core';
import { CloudIcon } from 'lucide-react';
import React from 'react';
import { getRemoteBadgeInfo } from '../data/remote/remote-transfers';
import { useRemoteStatus } from '../data/remote/use-remote-status';
import { RemoteStatusModal } from './remote-status-modal';

/**
 * Header badge of the remote bank: tells if the server is used and reachable, and if something needs a decision.
 * Not shown when the remote bank is not used.
 */
export const RemoteStatusBadge: React.FC = () => {
    const { status, isRemote } = useRemoteStatus();

    const [ opened, setOpened ] = React.useState(false);

    const info = status && getRemoteBadgeInfo(status, isRemote);
    if (!info) {
        return null;
    }

    return <>
        <Tooltip label={info.description} multiline maw={260}>
            <Badge
                component='button'
                type='button'
                color={info.color}
                variant='filled'
                leftSection={<CloudIcon size={12} />}
                onClick={() => setOpened(true)}
                style={{ cursor: 'pointer', flexShrink: 0 }}
            >
                {info.label}
            </Badge>
        </Tooltip>

        <RemoteStatusModal opened={opened} onClose={() => setOpened(false)} />
    </>;
};
