import { Alert, Card, PasswordInput, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { CheckIcon, CloudIcon, TriangleAlertIcon } from 'lucide-react';
import React from 'react';
import { getErrorMessage } from '../../data/remote/remote-transfers';
import { useReloadAll, useRefreshRemoteStatus, useRemoteStatus } from '../../data/remote/use-remote-status';
import { useDeviceSetConfig, useDeviceSetMode, useDeviceTestConnection } from '../../data/sdk/device/device.gen';
import { DeviceBankMode } from '../../data/sdk/model';
import { UIButton } from '../../ui/form/button/ui-button';
import { UIInputLabel } from '../../ui/form/ui-input-label';

type Message = { color: 'red' | 'green'; text: string };

/**
 * Android only: use the bank of a PKVault server instead of the one of the device.
 * The saves of the device are then sent to the server to be edited there, and written back once done.
 */
export const SettingsRemoteBank: React.FC = () => {
    const { available, status } = useRemoteStatus();

    const setConfigMutation = useDeviceSetConfig();
    const setModeMutation = useDeviceSetMode();
    const testMutation = useDeviceTestConnection();
    const refreshStatus = useRefreshRemoteStatus();
    const reloadAll = useReloadAll();

    const [ editedUrl, setEditedUrl ] = React.useState<string>();
    const [ token, setToken ] = React.useState('');
    const [ message, setMessage ] = React.useState<Message>();

    if (!available || !status) {
        return null;
    }

    const url = editedUrl ?? status.serverUrl ?? '';
    const configChanged = url.trim() !== (status.serverUrl ?? '') || token.length > 0;
    const hasServer = !!status.serverUrl;

    const insecure = url.trim().toLowerCase().startsWith('http://') && (token.length > 0 || status.hasToken);

    const run = async (action: () => Promise<Message | void>) => {
        setMessage(undefined);
        try {
            setMessage(await action() ?? undefined);
        } catch (e) {
            setMessage({ color: 'red', text: getErrorMessage(e) });
        }
    };

    // the form of the settings page must not be submitted from here
    const ignoreEnter = (e: React.KeyboardEvent) => {
        if (e.key === 'Enter') {
            e.preventDefault();
        }
    };

    const saveConfig = () => run(async () => {
        await setConfigMutation.mutateAsync({
            data: {
                serverUrl: url.trim(),
                // empty: unchanged, the token is never given back
                token: token.length > 0 ? token : undefined,
            },
        });
        setEditedUrl(undefined);
        setToken('');
        await refreshStatus();
        return { color: 'green', text: 'Saved' };
    });

    return <Card>
        <Stack>
            <UIInputLabel
                leftSection={<CloudIcon />}
                label='Remote bank'
                description='Use the bank of a PKVault server (ex: on your NAS) instead of the one of this device'
            />

            <SegmentedControl
                fullWidth
                value={status.mode}
                disabled={setModeMutation.isPending}
                data={[
                    { label: 'This device', value: DeviceBankMode.LOCAL },
                    { label: 'Server', value: DeviceBankMode.REMOTE },
                ]}
                onChange={mode => run(async () => {
                    await setModeMutation.mutateAsync({ params: { mode: mode as DeviceBankMode } });
                    // everything shown comes from the other bank now
                    await reloadAll();
                })}
            />

            <TextInput
                label='Server address'
                placeholder='https://my-nas:3000'
                value={url}
                onChange={e => setEditedUrl(e.currentTarget.value)}
                onKeyDown={ignoreEnter}
                autoCapitalize='none'
                autoCorrect='off'
                spellCheck={false}
            />

            <PasswordInput
                label='Access token'
                description='Only if the server requires one (PKVAULT_API_TOKEN)'
                placeholder={status.hasToken ? 'A token is saved. Type to replace it' : ''}
                value={token}
                onChange={e => setToken(e.currentTarget.value)}
                onKeyDown={ignoreEnter}
                autoCapitalize='none'
                autoCorrect='off'
                spellCheck={false}
            />

            {insecure &&
                <Alert variant='outline' color='yellow' icon={<TriangleAlertIcon />}>
                    This address is not encrypted: the token and your saves travel in clear. Prefer https, or a VPN.
                </Alert>}

            <UIButton
                name='remote-bank-save-config'
                controlLabel='Save'
                type='button'
                variant='filled'
                color='blue'
                disabled={!configChanged}
                onClick={saveConfig}
                fullWidth
            >
                Save
            </UIButton>

            <UIButton
                name='remote-bank-test'
                controlLabel='Test the connection'
                type='button'
                variant='default'
                disabled={!hasServer || configChanged}
                onClick={() => run(async () => {
                    const result = await testMutation.mutateAsync();
                    return { color: 'green', text: `Connected, PKVault server v${result.data.version}` };
                })}
                fullWidth
            >
                Test the connection
            </UIButton>

            {configChanged && hasServer && <Text size='sm' c='dimmed'>Save the changes to test them.</Text>}

            {message &&
                <Alert
                    variant='outline'
                    color={message.color}
                    icon={message.color === 'green' ? <CheckIcon /> : <TriangleAlertIcon />}
                >
                    {message.text}
                </Alert>}
        </Stack>
    </Card>;
};
