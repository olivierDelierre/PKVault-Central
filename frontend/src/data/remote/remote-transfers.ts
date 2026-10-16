import type { DeviceRemoteStatusDTO, DeviceTransferDTO } from '../sdk/model';
import { QueryError } from '../mutator/custom-instance';

/**
 * States of a transfer, as sent by the app (see DeviceTransferState on the backend).
 */
export const TransferState = {
    UPLOADING: 'UPLOADING',
    UPLOADED: 'UPLOADED',
    COMMIT_REQUESTED: 'COMMIT_REQUESTED',
    COMMITTED_ON_SERVER: 'COMMITTED_ON_SERVER',
    DOWNLOADED: 'DOWNLOADED',
    WRITING: 'WRITING',
    WRITTEN: 'WRITTEN',
    ACKED: 'ACKED',
    CLOSED: 'CLOSED',
    CONFLICT: 'CONFLICT',
} as const;

export const ConflictReason = {
    DEVICE_SAVE_CHANGED: 'DEVICE_SAVE_CHANGED',
    DEVICE_SAVE_MISSING: 'DEVICE_SAVE_MISSING',
    WRITE_FAILED: 'WRITE_FAILED',
    DELIVERY_GONE: 'DELIVERY_GONE',
} as const;

export const getTransferLabel = ({ state }: Pick<DeviceTransferDTO, 'state'>): string => {
    switch (state) {
        case TransferState.UPLOADING:
            return 'Sending the save to the server…';
        case TransferState.UPLOADED:
            return 'Open on the server';
        case TransferState.COMMIT_REQUESTED:
            return 'Waiting for the server…';
        case TransferState.COMMITTED_ON_SERVER:
            return 'Getting the modified save…';
        case TransferState.DOWNLOADED:
        case TransferState.WRITING:
            return 'Writing the modified save…';
        case TransferState.WRITTEN:
            return 'Confirming to the server…';
        case TransferState.ACKED:
            return 'Done';
        case TransferState.CLOSED:
            return 'Closed';
        case TransferState.CONFLICT:
            return 'Needs your decision';
        default:
            return state;
    }
};

export const getConflictExplanation = (reason: string | undefined): string => {
    switch (reason) {
        case ConflictReason.DEVICE_SAVE_CHANGED:
            return 'The save on this device changed after it was sent to the server (you played since). '
                + 'The changes made on the server were not written, to keep your progress.';
        case ConflictReason.DEVICE_SAVE_MISSING:
            return 'The save file of this device is missing.';
        case ConflictReason.WRITE_FAILED:
            return 'The modified save could not be written on this device. The previous save was put back.';
        case ConflictReason.DELIVERY_GONE:
            return 'The server does not have these changes anymore (its data may have been restored).';
        default:
            return 'This transfer needs a decision.';
    }
};

/** Waiting for the user, or finished with something to know. */
export const needsAttention = (transfer: DeviceTransferDTO): boolean =>
    transfer.state === TransferState.CONFLICT || !!transfer.warning;

/** Moving by itself: sending, getting or writing a save. */
export const isInProgress = (transfer: DeviceTransferDTO): boolean =>
    transfer.state !== TransferState.UPLOADED
    && transfer.state !== TransferState.CONFLICT
    && transfer.state !== TransferState.ACKED
    && transfer.state !== TransferState.CLOSED;

export type RemoteBadgeInfo = {
    color: string;
    label: string;
    description: string;
};

/**
 * What the status badge of the header tells. Null: nothing to say.
 */
export const getRemoteBadgeInfo = (status: DeviceRemoteStatusDTO, isRemote: boolean): RemoteBadgeInfo | null => {
    if (status.transfers.some(needsAttention)) {
        return { color: 'orange', label: 'Action needed', description: 'A transfer with the remote bank needs your decision' };
    }

    if (isRemote && !status.authorized) {
        return { color: 'red', label: 'Token refused', description: 'The server refused the access token' };
    }

    if (isRemote && !status.reachable) {
        return { color: 'red', label: 'Server offline', description: status.connectionError ?? 'The server cannot be reached' };
    }

    if (status.transfers.some(isInProgress)) {
        return { color: 'blue', label: 'Syncing…', description: 'A save is being exchanged with the server' };
    }

    if (isRemote) {
        return { color: 'green', label: 'Remote bank', description: 'Using the bank of the server' };
    }

    return null;
};

/** The message of an error of the API, to show to the user. */
export const getErrorMessage = (error: unknown): string => {
    if (error instanceof QueryError) {
        return error.errorMessage ?? error.statusText;
    }

    return error instanceof Error ? error.message : String(error);
};
