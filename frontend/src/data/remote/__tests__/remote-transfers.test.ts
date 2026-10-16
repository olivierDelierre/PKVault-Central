import { describe, expect, it } from 'vitest';
import type { DeviceRemoteStatusDTO, DeviceTransferDTO } from '../../sdk/model';
import {
    ConflictReason, getConflictExplanation, getRemoteBadgeInfo, getTransferLabel, isInProgress, needsAttention, TransferState,
} from '../remote-transfers';

const transfer = (overrides: Partial<DeviceTransferDTO> = {}): DeviceTransferDTO => ({
    localId: 'a',
    fileName: 'main.sav',
    state: TransferState.UPLOADED,
    localReloadPending: false,
    updatedAt: '2026-10-16T12:00:00',
    ...overrides,
});

const status = (overrides: Partial<DeviceRemoteStatusDTO> = {}): DeviceRemoteStatusDTO => ({
    mode: 'REMOTE',
    hasToken: true,
    reachable: true,
    authorized: true,
    transfers: [],
    ...overrides,
});

describe('getRemoteBadgeInfo', () => {
    it('says nothing when the remote bank is not used', () => {
        expect(getRemoteBadgeInfo(status({ mode: 'LOCAL', reachable: false }), false)).toBeNull();
    });

    it('shows the server as available when it is used and answers', () => {
        expect(getRemoteBadgeInfo(status(), true)).toMatchObject({ color: 'green', label: 'Remote bank' });
    });

    it('shows the server as offline, with the reason', () => {
        const info = getRemoteBadgeInfo(status({ reachable: false, connectionError: 'No route to host' }), true);

        expect(info).toMatchObject({ color: 'red', label: 'Server offline', description: 'No route to host' });
    });

    it('tells when the token is refused, rather than the server being offline', () => {
        const info = getRemoteBadgeInfo(status({ authorized: false, reachable: true }), true);

        expect(info).toMatchObject({ color: 'red', label: 'Token refused' });
    });

    it('asks for a decision before anything else', () => {
        const info = getRemoteBadgeInfo(status({
            reachable: false,
            transfers: [ transfer({ state: TransferState.CONFLICT }) ],
        }), true);

        expect(info).toMatchObject({ color: 'orange', label: 'Action needed' });
    });

    it('also asks for attention when a finished transfer has a warning, even in local mode', () => {
        const info = getRemoteBadgeInfo(status({
            mode: 'LOCAL',
            transfers: [ transfer({ state: TransferState.ACKED, warning: 'The server forgot this delivery' }) ],
        }), false);

        expect(info).toMatchObject({ color: 'orange' });
    });

    it('shows a transfer in progress, even in local mode (it goes on after a switch)', () => {
        const info = getRemoteBadgeInfo(status({
            mode: 'LOCAL',
            transfers: [ transfer({ state: TransferState.WRITING }) ],
        }), false);

        expect(info).toMatchObject({ color: 'blue', label: 'Syncing…' });
    });

    it('does not call a save waiting for edits a transfer in progress', () => {
        const info = getRemoteBadgeInfo(status({ transfers: [ transfer({ state: TransferState.UPLOADED }) ] }), true);

        expect(info).toMatchObject({ color: 'green' });
    });
});

describe('transfer states', () => {
    it('has a readable label for each state', () => {
        for (const state of Object.values(TransferState)) {
            const label = getTransferLabel({ state });
            expect(label).not.toBe(state);
            expect(label.length).toBeGreaterThan(0);
        }
    });

    it('keeps an unknown state readable instead of failing', () => {
        expect(getTransferLabel({ state: 'SOMETHING_NEW' })).toBe('SOMETHING_NEW');
    });

    it.each([
        [ TransferState.UPLOADING, true ],
        [ TransferState.COMMIT_REQUESTED, true ],
        [ TransferState.COMMITTED_ON_SERVER, true ],
        [ TransferState.DOWNLOADED, true ],
        [ TransferState.WRITING, true ],
        [ TransferState.WRITTEN, true ],
        [ TransferState.UPLOADED, false ],
        [ TransferState.CONFLICT, false ],
        [ TransferState.ACKED, false ],
        [ TransferState.CLOSED, false ],
    ])('isInProgress(%s) is %s', (state, expected) => {
        expect(isInProgress(transfer({ state }))).toBe(expected);
    });

    it('needs attention for a conflict or a warning only', () => {
        expect(needsAttention(transfer({ state: TransferState.CONFLICT }))).toBe(true);
        expect(needsAttention(transfer({ state: TransferState.ACKED, warning: 'x' }))).toBe(true);
        expect(needsAttention(transfer({ state: TransferState.WRITING, lastError: 'offline' }))).toBe(false);
    });

    it('explains every reason of conflict', () => {
        const explanations = Object.values(ConflictReason).map(getConflictExplanation);

        expect(new Set(explanations).size).toBe(explanations.length);
        expect(getConflictExplanation(undefined)).toBeTruthy();
    });
});
